using System.Text.Json;
using System.Text.Json.Nodes;
using Arr411.Core.Config;
using Arr411.Core.Http;
using Arr411.Core.Logging;
using Arr411.Core.Shell;

namespace Arr411.Core.Bridge;

/// <summary>
/// The native side of the web app: answers its <c>window.Native</c> calls (see Scripts\spa-adapter.js) and talks back to it.
/// Messages are JSON objects tagged with <c>t</c>. <see cref="OnMessage"/>, the shell callbacks and the outgoing calls
/// run on the UI thread; only the post callback may be reached from other threads.
/// </summary>
public sealed class SpaBridge : IDisposable
{
    /// <summary>Origin tag of the settings the web app saves, so that they are not sent back to it.</summary>
    public const string Origin = "spa";

    private static readonly TimeSpan BackTimeout = TimeSpan.FromMilliseconds(800);

    private readonly ConfigStore _config;
    private readonly NativeHttp _http;
    private readonly ISpaShell _shell;
    private readonly TabState _tabs;
    private readonly Action<string> _post;
    private readonly string _version;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<string, TaskCompletionSource<bool>> _backCalls = [];
    private readonly Queue<JsonNode> _queuedShares = new();
    private int _epoch;
    private int _lastCallId;
    private bool _foreground = true;
    private volatile bool _disposed;

    /// <param name="post">Sends a JSON message to the web app. Must be callable from any thread.</param>
    public SpaBridge(ConfigStore config, NativeHttp http, ISpaShell shell, TabState tabs, Action<string> post, string version)
    {
        _config = config;
        _http = http;
        _shell = shell;
        _tabs = tabs;
        _post = post;
        _version = version;
        _config.Changed += OnConfigChanged;
    }

    /// <summary>After this the bridge neither reads nor posts anything. Safe to call twice.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _config.Changed -= OnConfigChanged;
        _lifetime.Cancel();
        FailPendingCalls();
        _lifetime.Dispose();
    }

    /// <summary>
    /// What the web app needs synchronously at boot (settings, tab to open on, version): it fetches it before any of its own code runs,
    /// which also tells us that a new page was loaded.
    /// </summary>
    public string BuildState()
    {
        Interlocked.Increment(ref _epoch);
        _tabs.OnSpaStarted();
        FailPendingCalls();
        return JsonText.Stringify(new JsonObject
        {
            ["config"] = _config.ToJson(),
            ["tab"] = _tabs.TabForSpa(),
            ["version"] = _version,
        });
    }

    /// <summary>A message posted by the web app.</summary>
    public void OnMessage(string json)
    {
        if (_disposed || !MessageLimit.Accepts(json, "Web app")) return;

        // A bug while handling a message must not take the window down.
        try
        {
            using var document = JsonDocument.Parse(json);
            Dispatch(document.RootElement);
        }
        catch (Exception ex)
        {
            Log.Error("Web app message failed", ex);
        }
    }

    public void ShowTab(string tab) => Post(new JsonObject { ["t"] = "showTab", ["tab"] = tab });

    /// <summary>The user clicked the tab that is already shown: back to its root page.</summary>
    public void ReselectTab(string tab)
    {
        if (_tabs.SpaReady) Post(new JsonObject { ["t"] = "tabReselect", ["tab"] = tab });
    }

    /// <summary>Hands something to the web app (a magnet, files, a settings section); kept until it has finished booting.</summary>
    public void Share(JsonNode data)
    {
        if (_tabs.SpaReady) PostShared(data);
        else _queuedShares.Enqueue(data.DeepClone());
    }

    /// <summary>The window is (not) visible: the web app slows its polling down when it is not.</summary>
    public void SetForeground(bool on)
    {
        _foreground = on;
        if (_tabs.SpaReady) PostForeground();
    }

    /// <summary>The settings were changed outside the web app: it reads them again.</summary>
    public void ReloadConfig() => Post(new JsonObject { ["t"] = "loadCfg" });

    /// <summary>Asks the web app to go back one step. False when it has nothing to go back to, or does not answer.</summary>
    public async Task<bool> BackAsync()
    {
        if (_disposed) return false;

        var id = "b" + Interlocked.Increment(ref _lastCallId);
        var call = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_backCalls) _backCalls[id] = call;
        try
        {
            Post(new JsonObject { ["t"] = "back", ["id"] = id });
            return await call.Task.WaitAsync(BackTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return false;
        }
        finally
        {
            lock (_backCalls) _backCalls.Remove(id);
        }
    }

    private void Dispatch(JsonElement message)
    {
        switch (JsonText.Str(message, "t"))
        {
            case "http":
                StartHttp(message);
                break;
            case "saveConfig":
                SaveConfig(JsonText.Str(message, "patch"));
                break;
            case "selectTab":
                if (JsonText.Str(message, "tab") is { } tab && Tabs.IsTab(tab)) _shell.SelectTab(tab);
                break;
            case "openC411":
                if (JsonText.Str(message, "url") is { } c411Url) _shell.OpenC411(c411Url);
                break;
            case "reloadC411":
                _shell.ReloadC411();
                break;
            case "openUrl":
                if (JsonText.Str(message, "url") is { } url) _shell.OpenExternal(url);
                break;
            case "toast":
                if (JsonText.Str(message, "text") is { } toast) _shell.Toast(toast);
                break;
            case "setBadge":
                _shell.SetBadge(message.TryGetProperty("n", out var n) && n.ValueKind == JsonValueKind.Number && n.TryGetInt32(out var count) ? Math.Max(count, 0) : 0);
                break;
            case "copy":
                if (JsonText.Str(message, "text") is { } text) _shell.CopyText(text);
                break;
            case "drop":
                _shell.Dropped(DroppedContent.Parse(message));
                break;
            case "ready":
                OnReady();
                break;
            case "reply":
                OnReply(message);
                break;
        }
    }

    private void StartHttp(JsonElement message)
    {
        if (JsonText.Str(message, "id") is not { } id) return;

        // The answer belongs to the page that asked: it is dropped if that page was reloaded since.
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
                ["body"] = result.Text,
            });
        }
        catch (Exception ex)
        {
            Log.Error("HTTP answer not delivered", ex);
        }
    }

    private void SaveConfig(string? patchJson)
    {
        if (patchJson is null) return;

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(patchJson);
        }
        catch (JsonException)
        {
            return;
        }

        if (node is not JsonObject patch) return;

        // The web app holds a stale copy of the download tracking, which only the engine maintains.
        patch.Remove("tracked");
        _config.Merge(patch, Origin);
    }

    private void OnReady()
    {
        if (_tabs.OnSpaReady(out var tab)) ShowTab(tab);
        PostForeground();
        while (_queuedShares.TryDequeue(out var data)) PostShared(data);
    }

    private void OnReply(JsonElement message)
    {
        if (JsonText.Str(message, "id") is not { } id) return;

        TaskCompletionSource<bool>? call;
        lock (_backCalls) _backCalls.TryGetValue(id, out call);
        call?.TrySetResult(message.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.True);
    }

    private void OnConfigChanged(object? sender, ConfigChangedEventArgs e)
    {
        if (e.Origin == Origin) return;
        Post(new JsonObject { ["t"] = "config", ["patch"] = JsonNode.Parse(e.PatchJson) });
    }

    private void PostShared(JsonNode data) => Post(new JsonObject { ["t"] = "shared", ["data"] = data.DeepClone() });

    private void PostForeground() => Post(new JsonObject { ["t"] = "foreground", ["on"] = _foreground });

    private void Post(JsonObject message)
    {
        if (!_disposed) _post(JsonText.Stringify(message));
    }

    private void FailPendingCalls()
    {
        TaskCompletionSource<bool>[] calls;
        lock (_backCalls) calls = [.. _backCalls.Values];
        foreach (var call in calls) call.TrySetResult(false);
    }
}
