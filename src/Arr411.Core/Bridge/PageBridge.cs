using System.Buffers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Arr411.Core.Files;
using Arr411.Core.Http;
using Arr411.Core.Logging;
using Arr411.Core.Shell;

namespace Arr411.Core.Bridge;

/// <summary>
/// The native side of the C411 page: answers its <c>window.QbtPage</c> calls (see Scripts\page-adapter.js), the Windows counterpart of
/// the Android PageBridge. The page is a website: everything it asks is checked here, and it can only reach the engine
/// (through a short list of messages, served at a limited pace), the Downloads folder (the browser saves a few kinds of files there; the
/// page never writes a file itself) and the public Internet (through <see cref="PageFetcher"/>).
/// <para>
/// A page that nobody looks at has nothing to ask: what only reads (the categories, the state of qBittorrent) is served while the tab is on
/// screen and for a minute after it left; what does something (sends a torrent, arms a download, fetches a file, shows a notification,
/// reports a drop) is served only while the tab is on screen and the user did something with the window in the last
/// <see cref="GestureWindow"/> (<see cref="IUserPresence"/>): a page the user did not touch cannot act by itself.
/// </para>
/// Messages are JSON objects tagged with <c>t</c>. <see cref="OnMessage"/> and the shell callbacks run
/// on the UI thread; only the post callback may be reached from other threads.
/// </summary>
public sealed class PageBridge : IDisposable
{
    public const int MaxIdLength = 64;

    /// <summary>The most characters of a message of the page. What it says is short, except the torrent it uploads (<see cref="MaxUploadMessageChars"/>).</summary>
    public const int MaxMessageChars = 1024 * 1024;

    /// <summary>
    /// The most characters of the message that uploads a torrent: 16 MB, the most the page sends, are 21.4 million characters of base64
    /// (4 for 3 bytes), and the rest is the JSON around them.
    /// </summary>
    public const int MaxUploadMessageChars = 28 * 1024 * 1024;

    public const int MaxParallelFetches = 4;
    public const int MaxDropsPerWindow = 5;
    public const int MaxRequestsPerWindow = 30;
    public const int MaxUploadsPerWindow = 5;
    public const int MaxDownloadsPerWindow = 8;

    /// <summary>How long the engine has to say whether it takes a download over.</summary>
    public static readonly TimeSpan DefaultEngineTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long after the tab left the screen what only reads (the categories, the state of qBittorrent) is still served: the user often
    /// goes to look at qBittorrent and comes back. What acts is refused as soon as the tab is out of sight.
    /// </summary>
    public static readonly TimeSpan DefaultOffScreenGrace = TimeSpan.FromSeconds(60);

    /// <summary>How long the requests, the uploads, the drops and the downloads of the page are counted for the limits above.</summary>
    public static readonly TimeSpan DefaultRateWindow = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How recent the last key or mouse input must be for the page to act (same value as the Android app): what the user starts with a
    /// click ends within seconds, well inside it.
    /// </summary>
    public static readonly TimeSpan GestureWindow = TimeSpan.FromSeconds(30);

    private const int MaxUrlLength = 8 * 1024;
    private const int MaxNameLength = 260;
    private const int MaxToastLength = 140;

    // What the engine asks of the page at most in one qbitIndex (the same number as in the engine).
    private const int MaxHashesPerMessage = 300;

    // How much of a message is read to find its id or its kind without parsing it.
    private const int PeekChars = 2048;

    private static readonly SearchValues<char> HexDigits = SearchValues.Create("0123456789abcdefABCDEF");

    // Only these messages may come from the C411 page (no settings, no arbitrary servers). Same list as the Android app.
    private static readonly HashSet<string> PageTypes = ["getCategories", "pageStatus", "qbitIndex", "arm", "disarm", "upload", "arrMeta", "pagePrefs"];

    // Those of them that do something. The others only read, and are served while the tab is on screen and a minute after.
    private static readonly HashSet<string> ActingTypes = ["arm", "upload"];

    private static readonly string RefusedReply = ErrorReply("Message refusé");
    private static readonly string InternalReply = ErrorReply("Erreur interne");
    private static readonly string OffScreenReply = ErrorReply("Onglet C411 masqué : reviens sur la page et réessaie.");
    private static readonly string NotActiveReply = ErrorReply("Action refusée : l'onglet C411 n'est pas actif");
    private static readonly string TooManyRequestsReply = ErrorReply("Trop de requêtes d'un coup : réessaie.");
    private static readonly string TooManyUploadsReply = ErrorReply("Trop d'envois d'un coup : réessaie.");
    private static readonly string TooBigReply = ErrorReply("Message trop volumineux");

    private readonly IEngineClient _engine;
    private readonly IPageShell _shell;
    private readonly IUserPresence _presence;
    private readonly PageFetcher _fetcher;
    private readonly string _downloads;
    private readonly Action<string> _post;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Queue<long> _requests = new();
    private readonly Queue<long> _uploads = new();
    private readonly Queue<long> _drops = new();
    private readonly Queue<long> _saves = new();
    private readonly Queue<long> _toasts = new();
    private readonly Queue<long> _unread = new();
    private int _epoch;
    private int _fetching;
    private bool _onScreen = true;
    private long _leftScreenAt;
    private long _refusalLoggedAt;
    private volatile bool _disposed;

    /// <param name="presence">What Windows says about the user: the page acts only when the user did something with the window.</param>
    /// <param name="downloads">The folder the files saved for the page go to.</param>
    /// <param name="post">Sends a JSON message to the C411 page. Must be callable from any thread.</param>
    public PageBridge(IEngineClient engine, IPageShell shell, IUserPresence presence, PageFetcher fetcher, string downloads, Action<string> post)
    {
        _engine = engine;
        _shell = shell;
        _presence = presence;
        _fetcher = fetcher;
        _downloads = downloads;
        _post = post;
    }

    /// <summary>How long after the tab left the screen the page is still served what only reads. The default is the right value; the tests and the debug builds change it.</summary>
    public TimeSpan OffScreenGrace { get; init; } = DefaultOffScreenGrace;

    /// <summary>
    /// How long what the page does is counted for the limits. The default is the right value; the tests and the debug builds shorten it
    /// (a test sends in a second what a person sends in a minute, and the tests of a class share one app).
    /// </summary>
    public TimeSpan RateWindow { get; init; } = DefaultRateWindow;

    // Settable for the tests only: the real values are the defaults.
    internal TimeSpan EngineTimeout { get; init; } = DefaultEngineTimeout;

    /// <summary>After this the bridge neither reads nor posts anything. Safe to call twice.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    /// <summary>A new document is loading: its call ids start over, so the answers meant for the old one must not reach it.</summary>
    public void NewPage() => Interlocked.Increment(ref _epoch);

    /// <summary>
    /// Tells the bridge whether the user can see the page (its tab is the one on screen, in a window on screen). A page nobody looks at
    /// has nothing to ask of the engine, and nothing to do: once it has been out of sight for a while even what only reads is refused, and
    /// what acts is refused at once (see <see cref="GestureWindow"/>). UI thread.
    /// </summary>
    public void SetOnScreen(bool onScreen)
    {
        if (onScreen == _onScreen) return;

        _onScreen = onScreen;
        if (!onScreen) _leftScreenAt = Environment.TickCount64;
    }

    /// <summary>A message posted by the page.</summary>
    public void OnMessage(string json)
    {
        if (_disposed) return;

        // What a message costs is counted before anything is read of it: the page pays for every message it posts, whatever it says.
        if (!Allow(_requests, MaxRequestsPerWindow))
        {
            RefuseUnread(json, TooManyRequestsReply);
            return;
        }

        // The length is checked before the message is parsed (parsing tens of MB would freeze the window). Only the upload of a torrent is long.
        var large = json.Length > MaxMessageChars;
        if (large && !AdmitLarge(json)) return;

        // A bug while handling a message must not take the window down.
        try
        {
            using var document = JsonDocument.Parse(json);
            Dispatch(document.RootElement, large);
        }
        catch (Exception ex)
        {
            Log.Error("C411 page message failed", ex);
        }
    }

    /// <summary>An event of the engine for the page (a JSON object, as text).</summary>
    public void Emit(string json)
    {
        if (_disposed) return;
        _post(JsonText.Stringify(new JsonObject { ["t"] = "event", ["json"] = json }));
    }

    /// <summary>
    /// The browser starts a download of its own (the page did not go through the shim). Returns where to save it, or null to
    /// refuse it: when the engine takes the download over, because the page is about to hand it a .torrent, when the
    /// file is not of a kind the page may save, or when the page downloads too many files at once.
    /// </summary>
    public async Task<string?> PlanDownloadAsync(string url, string suggestedName)
    {
        if (_disposed) return null;

        var name = DownloadPolicy.SafeName(suggestedName);
        if (await EngineTakesDownloadAsync(url, name)) return null;

        if (!DownloadPolicy.IsAllowed(name))
        {
            _shell.Toast("Type de fichier refusé : " + name);
            return null;
        }

        // A page that downloads in a loop would fill the Downloads folder.
        if (!Allow(_saves, MaxDownloadsPerWindow))
        {
            _shell.Toast("Trop de téléchargements d'un coup : réessaie.");
            return null;
        }

        try
        {
            return DownloadPolicy.Reserve(_downloads, name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("Download refused, the Downloads folder is not usable: " + ex.GetType().Name);
            _shell.Toast("Téléchargement impossible : dossier inaccessible");
            return null;
        }
    }

    /// <summary>The download planned by <see cref="PlanDownloadAsync"/> ended.</summary>
    public void DownloadFinished(string path, bool success)
    {
        var name = Path.GetFileName(path);
        if (success)
        {
            _shell.Toast("Enregistré dans Téléchargements : " + name);
            return;
        }

        DownloadPolicy.DiscardIfEmpty(path);
        _shell.Toast("Téléchargement impossible : " + name);
    }

    private void Dispatch(JsonElement message, bool large)
    {
        var kind = JsonText.Str(message, "t");
        if (large && kind != "send") return; // only the upload of a torrent is that long

        // There is no "saveFile": the page may not write a file of its own. What it downloads is saved by the browser, which marks the
        // file as coming from the Internet (see PlanDownloadAsync). Unknown messages are ignored.
        switch (kind)
        {
            case "send":
                Send(message, large);
                break;
            case "download":
                Download(message);
                break;
            case "toast":
                Toast(message);
                break;
            case "fetchBytes":
                FetchBytes(message);
                break;
            case "drop":
                Drop(message);
                break;
        }
    }

    // A message of more than MaxMessageChars is read only when it is the upload of a torrent that the user is asking for. What is checked here
    // costs nothing: the start of the text, the counters, Windows. The page gets its answer when it carries an id.
    private bool AdmitLarge(string json)
    {
        if (json.Length > MaxUploadMessageChars || !LooksLikeUpload(json))
        {
            Log.Warn($"C411 page message refused: {json.Length} characters");
            RefuseUnread(json, TooBigReply);
            return false;
        }

        if (!CanAct("upload"))
        {
            RefuseUnread(json, NotActiveReply);
            return false;
        }

        if (!Allow(_uploads, MaxUploadsPerWindow))
        {
            RefuseUnread(json, TooManyUploadsReply);
            return false;
        }

        return true;
    }

    // The page waits for the answer of what it sent, so a message that is refused without being read is still answered when its id can be seen
    // at the start of it (its keys are not in a known order). A page that floods is not answered more than the window allows.
    private void RefuseUnread(string json, string reply)
    {
        if (!TryPeekId(json, out var id) || !Allow(_unread, MaxRequestsPerWindow)) return;

        PostReply(id, reply, Volatile.Read(ref _epoch));
    }

    private static bool LooksLikeUpload(string json) =>
        json.AsSpan(0, Math.Min(json.Length, PeekChars)).IndexOf("upload", StringComparison.Ordinal) >= 0;

    private static bool TryPeekId(string json, out string id)
    {
        const string key = "\"id\":\"";
        id = "";
        var head = json.AsSpan(0, Math.Min(json.Length, PeekChars));
        var at = head.IndexOf(key, StringComparison.Ordinal);
        if (at < 0) return false;

        var rest = head[(at + key.Length)..];
        var length = rest.IndexOf('"');
        if (length is <= 0 or > MaxIdLength || rest[..length].Contains('\\')) return false;

        id = rest[..length].ToString();
        return true;
    }

    // What the page does for itself (not what it reads) needs the user: the tab is on screen, the window is the one in front and a key or a
    // button was pressed a moment ago. The page cannot fake any of it. There is no grace here: a tab that left the screen cannot act.
    private bool CanAct(string what)
    {
        if (_onScreen && _presence.HasRecentGesture(GestureWindow)) return true;

        LogRefusal(what);
        return false;
    }

    // A drop does not bring the window to the front: the pointer over it is what the window can see (see IUserPresence).
    private bool CanDrop()
    {
        if (_onScreen && _presence.HasRecentDrop(GestureWindow)) return true;

        LogRefusal("drop");
        return false;
    }

    // A page that is refused in a loop would fill the log: one line in a window is enough to tell what happens.
    private void LogRefusal(string what)
    {
        var now = Environment.TickCount64;
        if (_refusalLoggedAt != 0 && now - _refusalLoggedAt < RateWindow.TotalMilliseconds) return;

        _refusalLoggedAt = now;
        Log.Warn($"C411 page: \"{what}\" refused, the tab is not on screen or the user did nothing in the last {GestureWindow.TotalSeconds:0} s");
    }

    // A notification the page shows is a text the user reads as coming from the app: the page may not make up one by itself, nor fill the screen
    // with them. One per 3 s with the default window.
    private TimeSpan ToastInterval => TimeSpan.FromTicks(RateWindow.Ticks * 3 / 10);

    private void Toast(JsonElement message)
    {
        if (JsonText.Str(message, "text") is not { } text || !CanAct("toast")) return;

        if (Allow(_toasts, 1, ToastInterval)) _shell.Toast(Truncate(text, MaxToastLength));
    }

    // A page that reports drops in a loop would bury the user under sheets to confirm.
    private void Drop(JsonElement message)
    {
        if (!CanDrop()) return;

        if (!Allow(_drops, MaxDropsPerWindow))
        {
            _shell.Toast("Trop de dépôts d'un coup : réessaie.");
            return;
        }

        _shell.Dropped(DroppedContent.Parse(message));
    }

    private void Send(JsonElement message, bool uploadCounted)
    {
        if (!TryId(message, out var id)) return;

        var epoch = Volatile.Read(ref _epoch);
        if (!_onScreen && Environment.TickCount64 - _leftScreenAt >= OffScreenGrace.TotalMilliseconds)
        {
            PostReply(id, OffScreenReply, epoch);
            return;
        }

        if (!TrySanitize(JsonText.Str(message, "msg"), out var safe, out var type) || (uploadCounted && type != "upload"))
        {
            PostReply(id, RefusedReply, epoch);
            return;
        }

        if (ActingTypes.Contains(type) && !CanAct(type))
        {
            PostReply(id, NotActiveReply, epoch);
            return;
        }

        // A page that sends torrents in a loop would fill qBittorrent with them. (A long message was counted before it was read.)
        if (type == "upload" && !uploadCounted && !Allow(_uploads, MaxUploadsPerWindow))
        {
            PostReply(id, TooManyUploadsReply, epoch);
            return;
        }

        _ = RelayAsync(id, safe, epoch);
    }

    private async Task RelayAsync(string id, string message, int epoch)
    {
        string reply;
        try
        {
            reply = await _engine.SendAsync(message, _lifetime.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            return;
        }
        catch (Exception ex)
        {
            Log.Error("Engine request of the C411 page failed", ex);
            reply = InternalReply;
        }

        PostReply(id, reply, epoch);
    }

    private void Download(JsonElement message)
    {
        if (JsonText.Str(message, "url") is not { Length: > 0 and <= MaxUrlLength } url || !CanAct("download")) return;

        var filename = Truncate(JsonText.Str(message, "filename") ?? "", MaxNameLength);
        _ = DownloadAsync(url, filename);
    }

    private async Task DownloadAsync(string url, string filename)
    {
        if (await EngineTakesDownloadAsync(url, filename) || _disposed) return;

        _shell.Toast("Délai dépassé : réessaie.");
    }

    // The page just clicked a download: when it is armed (the user pressed one of our buttons) the engine takes it over.
    private async Task<bool> EngineTakesDownloadAsync(string url, string filename)
    {
        if (_disposed) return false;

        var request = JsonText.Stringify(new JsonObject { ["type"] = "__download", ["url"] = url, ["filename"] = filename });
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            timeout.CancelAfter(EngineTimeout);

            var reply = await _engine.SendAsync(request, timeout.Token);
            using var document = JsonDocument.Parse(reply);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("handled", out var handled)
                && handled.ValueKind == JsonValueKind.True;
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or JsonException)
        {
            return false;
        }
    }

    // A page that repeats a request in a loop would flood the user's servers, fill qBittorrent or bury the window.
    private bool Allow(Queue<long> recent, int max) => Allow(recent, max, RateWindow);

    private static bool Allow(Queue<long> recent, int max, TimeSpan window)
    {
        var now = Environment.TickCount64;
        while (recent.Count > 0 && now - recent.Peek() > window.TotalMilliseconds) recent.Dequeue();
        if (recent.Count >= max) return false;

        recent.Enqueue(now);
        return true;
    }

    private void FetchBytes(JsonElement message)
    {
        if (!TryId(message, out var id)) return;

        var epoch = Volatile.Read(ref _epoch);
        if (!CanAct("fetchBytes"))
        {
            PostReply(id, NotActiveReply, epoch);
            return;
        }

        if (Interlocked.Increment(ref _fetching) > MaxParallelFetches)
        {
            Interlocked.Decrement(ref _fetching);
            PostReply(id, ErrorReply("Trop de téléchargements en cours"), epoch);
            return;
        }

        _ = FetchBytesAsync(id, JsonText.Str(message, "url"), JsonText.Str(message, "referer"), _shell.UserAgent, epoch);
    }

    private async Task FetchBytesAsync(string id, string? url, string? referer, string userAgent, int epoch)
    {
        try
        {
            var result = await _fetcher.FetchAsync(url, referer, userAgent, _shell.CookieHeaderAsync, _lifetime.Token).ConfigureAwait(false);
            var reply = result.Error is { } error
                ? new JsonObject { ["error"] = error }
                : new JsonObject { ["status"] = result.Status, ["base64"] = Convert.ToBase64String(result.Body) };
            PostReply(id, JsonText.Stringify(reply), epoch);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error("Download for the C411 page failed", ex);
            PostReply(id, InternalReply, epoch);
        }
        finally
        {
            Interlocked.Decrement(ref _fetching);
        }
    }

    private void PostReply(string id, string json, int epoch)
    {
        if (_disposed || epoch != Volatile.Read(ref _epoch)) return;

        _post(JsonText.Stringify(new JsonObject { ["t"] = "reply", ["id"] = id, ["json"] = json }));
    }

    /// <summary>The message as the engine may receive it (and its type), or false when the page is not allowed to send it.</summary>
    private static bool TrySanitize(string? messageJson, out string safe, out string type)
    {
        safe = type = "";
        if (messageJson is null || messageJson.Length > MaxUploadMessageChars) return false;

        try
        {
            if (JsonNode.Parse(messageJson) is not JsonObject message) return false;
            if (JsonText.Str(message["type"]) is not { } kind || !PageTypes.Contains(kind)) return false;
            if (!HashesAreValid(kind, message)) return false;

            message.Remove("cfg"); // arrMeta always uses the saved settings
            safe = JsonText.Stringify(message);
            type = kind;
            return true;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    // The engine puts a hash in the address it asks qBittorrent (it looks a torrent up by it): nothing else may get there.
    private static bool HashesAreValid(string kind, JsonObject message)
    {
        switch (kind)
        {
            case "pageStatus":
                if (!message.TryGetPropertyValue("hash", out var hash) || hash is null) return true;
                return JsonText.Str(hash) is { } text && (text.Length == 0 || IsHash(text));
            case "qbitIndex":
                if (!message.TryGetPropertyValue("hashes", out var list) || list is null) return true;
                if (list is not JsonArray { Count: <= MaxHashesPerMessage } hashes) return false;

                foreach (var item in hashes)
                {
                    if (JsonText.Str(item) is not { } candidate || !IsHash(candidate)) return false;
                }

                return true;
            default:
                return true;
        }
    }

    // The infohash of a torrent: 40 hexadecimal digits (v1) or 64 (v2).
    private static bool IsHash(string text) => text.Length is 40 or 64 && text.AsSpan().IndexOfAnyExcept(HexDigits) < 0;

    private static bool TryId(JsonElement message, out string id)
    {
        id = JsonText.Str(message, "id") ?? "";
        return id.Length is > 0 and <= MaxIdLength;
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max];

    private static string ErrorReply(string error) => JsonText.Stringify(new JsonObject { ["ok"] = false, ["error"] = error });
}
