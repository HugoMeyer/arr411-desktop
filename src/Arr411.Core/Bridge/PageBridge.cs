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
/// (through a short list of messages, served at a limited pace, and only while the tab is on screen or just left it), the Downloads folder
/// (the browser saves a few kinds of files there; the page never writes a file itself) and the public Internet (through
/// <see cref="PageFetcher"/>). Messages are JSON objects tagged with <c>t</c>. <see cref="OnMessage"/> and the shell callbacks run
/// on the UI thread; only the post callback may be reached from other threads.
/// </summary>
public sealed class PageBridge : IDisposable
{
    public const int MaxIdLength = 64;
    public const int MaxMessageChars = 48 * 1024 * 1024;
    public const int MaxParallelFetches = 4;
    public const int MaxDropsPerWindow = 5;
    public const int MaxRequestsPerWindow = 30;
    public const int MaxUploadsPerWindow = 5;
    public const int MaxDownloadsPerWindow = 8;

    /// <summary>How long the engine has to say whether it takes a download over.</summary>
    public static readonly TimeSpan DefaultEngineTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long after the tab left the screen its requests are still served: the user who sends a torrent and goes to look at qBittorrent
    /// right away must not lose it.
    /// </summary>
    public static readonly TimeSpan DefaultOffScreenGrace = TimeSpan.FromSeconds(60);

    /// <summary>How long the requests, the uploads, the drops and the downloads of the page are counted for the limits above.</summary>
    public static readonly TimeSpan DefaultRateWindow = TimeSpan.FromSeconds(10);

    private const int MaxUrlLength = 8 * 1024;
    private const int MaxNameLength = 260;
    private const int MaxToastLength = 200;

    // Only these messages may come from the C411 page (no settings, no arbitrary servers). Same list as the Android app.
    private static readonly HashSet<string> PageTypes = ["getCategories", "pageStatus", "qbitIndex", "arm", "disarm", "upload", "arrMeta"];

    private static readonly string RefusedReply = ErrorReply("Message refusé");
    private static readonly string InternalReply = ErrorReply("Erreur interne");
    private static readonly string OffScreenReply = ErrorReply("Onglet C411 masqué : reviens sur la page et réessaie.");
    private static readonly string TooManyRequestsReply = ErrorReply("Trop de requêtes d'un coup : réessaie.");
    private static readonly string TooManyUploadsReply = ErrorReply("Trop d'envois d'un coup : réessaie.");

    private readonly IEngineClient _engine;
    private readonly IPageShell _shell;
    private readonly PageFetcher _fetcher;
    private readonly string _downloads;
    private readonly Action<string> _post;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Queue<long> _requests = new();
    private readonly Queue<long> _uploads = new();
    private readonly Queue<long> _drops = new();
    private readonly Queue<long> _saves = new();
    private int _epoch;
    private int _fetching;
    private bool _onScreen = true;
    private long _leftScreenAt;
    private volatile bool _disposed;

    /// <param name="downloads">The folder the files saved for the page go to.</param>
    /// <param name="post">Sends a JSON message to the C411 page. Must be callable from any thread.</param>
    public PageBridge(IEngineClient engine, IPageShell shell, PageFetcher fetcher, string downloads, Action<string> post)
    {
        _engine = engine;
        _shell = shell;
        _fetcher = fetcher;
        _downloads = downloads;
        _post = post;
    }

    /// <summary>How long after the tab left the screen the page is still served. The default is the right value; the tests and the debug builds change it.</summary>
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
    /// has nothing to ask of the engine: once it has been out of sight for a while, what it asks is not what the user wants. UI thread.
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
        if (_disposed || !MessageLimit.Accepts(json, "C411 page")) return;

        // A bug while handling a message must not take the window down.
        try
        {
            using var document = JsonDocument.Parse(json);
            Dispatch(document.RootElement);
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

    private void Dispatch(JsonElement message)
    {
        // There is no "saveFile": the page may not write a file of its own. What it downloads is saved by the browser, which marks the
        // file as coming from the Internet (see PlanDownloadAsync). Unknown messages are ignored.
        switch (JsonText.Str(message, "t"))
        {
            case "send":
                Send(message);
                break;
            case "download":
                Download(message);
                break;
            case "toast":
                if (JsonText.Str(message, "text") is { } text) _shell.Toast(Truncate(text, MaxToastLength));
                break;
            case "fetchBytes":
                FetchBytes(message);
                break;
            case "drop":
                Drop(message);
                break;
        }
    }

    // A page that reports drops in a loop would bury the user under sheets to confirm.
    private void Drop(JsonElement message)
    {
        if (!Allow(_drops, MaxDropsPerWindow))
        {
            _shell.Toast("Trop de dépôts d'un coup : réessaie.");
            return;
        }

        _shell.Dropped(DroppedContent.Parse(message));
    }

    private void Send(JsonElement message)
    {
        if (!TryId(message, out var id)) return;

        var epoch = Volatile.Read(ref _epoch);
        if (!_onScreen && Environment.TickCount64 - _leftScreenAt >= OffScreenGrace.TotalMilliseconds)
        {
            PostReply(id, OffScreenReply, epoch);
            return;
        }

        // A page that asks in a loop would flood the servers of the user.
        if (!Allow(_requests, MaxRequestsPerWindow))
        {
            PostReply(id, TooManyRequestsReply, epoch);
            return;
        }

        if (!TrySanitize(JsonText.Str(message, "msg"), out var safe, out var type))
        {
            PostReply(id, RefusedReply, epoch);
            return;
        }

        // ... and one that sends torrents in a loop would fill qBittorrent with them.
        if (type == "upload" && !Allow(_uploads, MaxUploadsPerWindow))
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
        if (JsonText.Str(message, "url") is not { Length: > 0 and <= MaxUrlLength } url) return;

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
    private bool Allow(Queue<long> recent, int max)
    {
        var now = Environment.TickCount64;
        while (recent.Count > 0 && now - recent.Peek() > RateWindow.TotalMilliseconds) recent.Dequeue();
        if (recent.Count >= max) return false;

        recent.Enqueue(now);
        return true;
    }

    private void FetchBytes(JsonElement message)
    {
        if (!TryId(message, out var id)) return;

        var epoch = Volatile.Read(ref _epoch);
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
        if (messageJson is null || messageJson.Length > MaxMessageChars) return false;

        try
        {
            if (JsonNode.Parse(messageJson) is not JsonObject message) return false;
            if (JsonText.Str(message["type"]) is not { } kind || !PageTypes.Contains(kind)) return false;

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

    private static bool TryId(JsonElement message, out string id)
    {
        id = JsonText.Str(message, "id") ?? "";
        return id.Length is > 0 and <= MaxIdLength;
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max];

    private static string ErrorReply(string error) => JsonText.Stringify(new JsonObject { ["ok"] = false, ["error"] = error });
}
