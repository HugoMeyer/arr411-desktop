using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Arr411.Core.Config;
using Arr411.Core.Http;
using Arr411.Core.Logging;

namespace Arr411.Core.Bridge;

/// <summary>
/// The native side of the engine, the hidden page that runs the extension's background logic (see Scripts\engine-adapter.js):
/// it serves the page's storage, network and notification calls, and hands it the requests of the C411 page.
/// Messages are JSON objects tagged with <c>t</c>. <see cref="OnMessage"/> and the shell callbacks run on the UI thread;
/// the post callback and <see cref="SendAsync"/> may be reached from any thread.
/// </summary>
public sealed class EngineBridge : IEngineClient, IDisposable
{
    /// <summary>Origin tag of the settings the engine saves, so that they are not sent back to it.</summary>
    public const string Origin = "engine";

    // Same wording as the Android app.
    private static readonly string RestartedReply = ErrorReply("Le moteur de l'app a redémarré, réessaie.");
    private static readonly string StoppedReply = ErrorReply("Le moteur de l'app est arrêté.");
    private static readonly string InvalidReply = ErrorReply("Réponse invalide du moteur.");

    private readonly ConfigStore _config;
    private readonly NativeHttp _http;
    private readonly IEngineShell _shell;
    private readonly Action<string> _post;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _gate = new();
    private readonly List<Call> _waiting = [];
    private readonly Dictionary<string, Call> _delivered = [];
    private int _epoch;
    private long _lastCallId;
    private bool _ready;
    private volatile bool _disposed;

    /// <param name="post">Sends a JSON message to the engine page. Must be callable from any thread.</param>
    public EngineBridge(ConfigStore config, NativeHttp http, IEngineShell shell, Action<string> post)
    {
        _config = config;
        _http = http;
        _shell = shell;
        _post = post;
        _config.Changed += OnConfigChanged;
    }

    /// <summary>After this the bridge neither reads nor posts anything, and every request still waiting is answered with an error. Safe to call twice.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _config.Changed -= OnConfigChanged;
        _lifetime.Cancel();
        CompleteAll(StoppedReply);
        _lifetime.Dispose();
    }

    /// <summary>
    /// What the engine needs synchronously at boot (the settings): it fetches it before any of its own code runs,
    /// which also tells us that a new page was loaded.
    /// </summary>
    public string BuildState()
    {
        PageLost();
        return JsonText.Stringify(new JsonObject { ["config"] = _config.ToJson() });
    }

    /// <summary>
    /// The engine page is gone (crashed, reloaded). The requests it was working on get an error; the ones it never received
    /// wait for the next page.
    /// </summary>
    public void PageLost()
    {
        List<Call> lost;
        lock (_gate)
        {
            _ready = false;
            Interlocked.Increment(ref _epoch);
            lost = [.. _delivered.Values];
            _delivered.Clear();
        }

        foreach (var call in lost) call.Completion.TrySetResult(RestartedReply);
    }

    /// <summary>A message posted by the engine page.</summary>
    public void OnMessage(string json)
    {
        if (_disposed || !MessageLimit.Accepts(json, "Engine")) return;

        // A bug while handling a message must not take the window down.
        try
        {
            using var document = JsonDocument.Parse(json);
            Dispatch(document.RootElement);
        }
        catch (Exception ex)
        {
            Log.Error("Engine message failed", ex);
        }
    }

    /// <summary>
    /// Hands a message (what the extension's pages send to its background script, as JSON text) to the engine and returns its answer
    /// (JSON text too). Waits for the engine to be up. When the engine goes away the answer is an error object, like on Android.
    /// </summary>
    public async Task<string> SendAsync(string messageJson, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();

        var call = new Call("m" + Interlocked.Increment(ref _lastCallId).ToString(CultureInfo.InvariantCulture), messageJson);
        bool deliver;
        lock (_gate)
        {
            if (_disposed) return StoppedReply;

            deliver = _ready;
            if (deliver) _delivered[call.Id] = call;
            else _waiting.Add(call);
        }

        using var registration = cancellation.Register(() => Abandon(call, cancellation));
        if (deliver) PostHandle(call);
        return await call.Completion.Task.ConfigureAwait(false);
    }

    private void Dispatch(JsonElement message)
    {
        switch (JsonText.Str(message, "t"))
        {
            case "ready":
                OnReady();
                break;
            case "reply":
                OnReply(message);
                break;
            case "emit":
                if (JsonText.Str(message, "json") is { } emitted) _shell.Emit(emitted);
                break;
            case "storageSet":
                if (JsonText.Str(message, "json") is { } patch) _config.Merge(patch, Origin);
                break;
            case "openUrl":
                if (JsonText.Str(message, "url") is { } url) _shell.OpenExternal(url);
                break;
            case "notify":
                _shell.Notify(JsonText.Str(message, "title") ?? "", JsonText.Str(message, "text") ?? "");
                break;
            case "http":
                StartHttp(message);
                break;
        }
    }

    private void OnReady()
    {
        Call[] waiting;
        lock (_gate)
        {
            _ready = true;
            waiting = [.. _waiting];
            _waiting.Clear();
            foreach (var call in waiting) _delivered[call.Id] = call;
        }

        foreach (var call in waiting) PostHandle(call);
    }

    private void OnReply(JsonElement message)
    {
        if (JsonText.Str(message, "id") is not { } id) return;

        Call? call;
        lock (_gate)
        {
            if (!_delivered.Remove(id, out call)) return;
        }

        call.Completion.TrySetResult(JsonText.Str(message, "json") ?? InvalidReply);
    }

    private void StartHttp(JsonElement message)
    {
        if (JsonText.Str(message, "id") is not { } id) return;

        // The ids of the page restart with each page: the answer is dropped if the page that asked is gone.
        var epoch = Volatile.Read(ref _epoch);
        var request = JsonText.Str(message, "req") ?? "";
        _ = RunHttpAsync(id, request, epoch);
    }

    private async Task RunHttpAsync(string id, string request, int epoch)
    {
        try
        {
            var result = await _http.SendAsync(request, _lifetime.Token).ConfigureAwait(false);
            if (epoch != Volatile.Read(ref _epoch) || _lifetime.IsCancellationRequested) return;

            Post(new JsonObject
            {
                ["t"] = "httpDone",
                ["id"] = id,
                ["status"] = result.Status,
                ["error"] = result.Error,
                ["text"] = result.Text,
            });
        }
        catch (Exception ex)
        {
            Log.Error("HTTP answer not delivered", ex);
        }
    }

    private void OnConfigChanged(object? sender, ConfigChangedEventArgs e)
    {
        if (e.Origin == Origin) return;
        Post(new JsonObject { ["t"] = "config", ["patch"] = JsonNode.Parse(e.PatchJson) });
    }

    private void Abandon(Call call, CancellationToken cancellation)
    {
        lock (_gate)
        {
            _waiting.Remove(call);
            _delivered.Remove(call.Id);
        }

        call.Completion.TrySetCanceled(cancellation);
    }

    private void CompleteAll(string reply)
    {
        Call[] calls;
        lock (_gate)
        {
            calls = [.. _waiting, .. _delivered.Values];
            _waiting.Clear();
            _delivered.Clear();
        }

        foreach (var call in calls) call.Completion.TrySetResult(reply);
    }

    private void PostHandle(Call call) => Post(new JsonObject { ["t"] = "handle", ["id"] = call.Id, ["raw"] = call.Message });

    private void Post(JsonObject message)
    {
        if (!_disposed) _post(JsonText.Stringify(message));
    }

    private static string ErrorReply(string error) => JsonText.Stringify(new JsonObject { ["ok"] = false, ["error"] = error });

    private sealed class Call(string id, string message)
    {
        public string Id { get; } = id;

        public string Message { get; } = message;

        public TaskCompletionSource<string> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
