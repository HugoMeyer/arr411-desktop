using System.Net;
using System.Text.Json.Nodes;
using Arr411.Core.Bridge;
using Arr411.Core.Config;
using Arr411.Core.Http;

namespace Arr411.Core.Tests;

/// <summary>Records what the engine asked the window to do.</summary>
internal sealed class FakeEngineShell : IEngineShell
{
    private readonly List<string> _calls = [];

    public string[] Calls
    {
        get
        {
            lock (_calls) return [.. _calls];
        }
    }

    /// <summary>When set, every call throws it (a window that misbehaves).</summary>
    public Exception? Fault { get; set; }

    public void Notify(string title, string text) => Add("notify:" + title + "|" + text);

    public void OpenExternal(string url) => Add("openExternal:" + url);

    public void Emit(string json) => Add("emit:" + json);

    private void Add(string call)
    {
        if (Fault is not null) throw Fault;
        lock (_calls) _calls.Add(call);
    }
}

/// <summary>An engine bridge wired to fakes: a stub network, a throw-away settings file, a recording window and a collector for what it posts.</summary>
internal sealed class EngineHarness : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly NativeHttp _net;
    private readonly List<JsonObject> _posted = [];

    public EngineHarness(Func<RecordedRequest, CancellationToken, Task<HttpResponseMessage>>? network = null)
    {
        Config = new ConfigStore(_dir.File("config.dat"), new ReversibleProtector());
        Network = new StubHandler(network ?? ((_, _) => Task.FromResult(StubHandler.Reply(HttpStatusCode.OK, "Ok."u8.ToArray()))));
        _net = new NativeHttp("UA", Network);
        Bridge = new EngineBridge(Config, _net, Shell, Collect);
    }

    public ConfigStore Config { get; }

    public StubHandler Network { get; }

    public FakeEngineShell Shell { get; } = new();

    public EngineBridge Bridge { get; }

    public JsonObject[] Posted
    {
        get
        {
            lock (_posted) return [.. _posted];
        }
    }

    public JsonObject[] Handles => [.. Posted.Where(p => (string?)p["t"] == "handle")];

    public async Task<JsonObject> WaitForAsync(Func<JsonObject, bool> match, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            var found = Posted.FirstOrDefault(match);
            if (found is not null) return found;
            await Task.Delay(10);
        }

        Assert.Fail("No matching message was posted. Posted: " + string.Join(" | ", Posted.Select(p => p.ToJsonString())));
        return null!;
    }

    public void Dispose()
    {
        Bridge.Dispose();
        _net.Dispose();
        Network.Dispose();
        _dir.Dispose();
    }

    private void Collect(string json)
    {
        var message = JsonNode.Parse(json)!.AsObject();
        lock (_posted) _posted.Add(message);
    }
}

public sealed class EngineBridgeTests : IDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    private readonly EngineHarness _h = new();

    public void Dispose() => _h.Dispose();

    private static string Message(string type, params (string Name, string Value)[] fields)
    {
        var message = new JsonObject { ["t"] = type };
        foreach (var (name, value) in fields) message[name] = value;
        return message.ToJsonString();
    }

    private static string HttpCall(string id, string url) => new JsonObject
    {
        ["t"] = "http",
        ["id"] = id,
        ["req"] = new JsonObject
        {
            ["url"] = url,
            ["method"] = "GET",
            ["headers"] = new JsonObject(),
            ["body"] = new JsonObject { ["type"] = "none" },
        }.ToJsonString(),
    }.ToJsonString();

    private static Func<JsonObject, bool> HttpDone(string id) => p => (string?)p["t"] == "httpDone" && (string?)p["id"] == id;

    /// <summary>A page of the engine has just asked for its state.</summary>
    private void Boot() => _h.Bridge.BuildState();

    private void Ready() => _h.Bridge.OnMessage("""{"t":"ready"}""");

    private void Reply(string id, string json) => _h.Bridge.OnMessage(Message("reply", ("id", id), ("json", json)));

    private string LastHandleId() => (string)_h.Handles.Last()["id"]!;

    private static async Task<JsonNode> Answer(Task<string> call) => JsonNode.Parse(await call.WaitAsync(Patience))!;

    private static void AssertRestartError(JsonNode reply)
    {
        Assert.False((bool)reply["ok"]!);
        Assert.Equal("Le moteur de l'app a redémarré, réessaie.", (string?)reply["error"]);
    }

    [Fact]
    public void The_boot_state_carries_the_settings()
    {
        _h.Config.Merge("""{"qbUrl":"http://q:8080","title":"été"}""", "test");

        var state = JsonNode.Parse(_h.Bridge.BuildState())!;
        var config = JsonNode.Parse((string)state["config"]!)!;

        Assert.Equal("http://q:8080", (string?)config["qbUrl"]);
        Assert.Equal("été", (string?)config["title"]);
    }

    [Fact]
    public async Task A_request_waits_for_the_engine_then_gets_its_answer()
    {
        Boot();
        var call = _h.Bridge.SendAsync("""{"type":"listTorrents"}""");

        Assert.False(call.IsCompleted);
        Assert.Empty(_h.Handles);

        Ready();
        var handle = Assert.Single(_h.Handles);
        Assert.Equal("""{"type":"listTorrents"}""", (string?)handle["raw"]);

        Reply((string)handle["id"]!, """{"ok":true,"torrents":[]}""");
        Assert.Equal("""{"ok":true,"torrents":[]}""", await call.WaitAsync(Patience));
    }

    [Fact]
    public void A_request_to_a_ready_engine_is_posted_at_once()
    {
        Boot();
        Ready();

        _ = _h.Bridge.SendAsync("""{"type":"pageStatus"}""");

        Assert.Equal("""{"type":"pageStatus"}""", (string?)Assert.Single(_h.Handles)["raw"]);
    }

    [Fact]
    public async Task Requests_are_answered_independently_and_in_any_order()
    {
        Boot();
        Ready();
        var first = _h.Bridge.SendAsync("1");
        var second = _h.Bridge.SendAsync("2");
        var firstId = (string)_h.Handles[0]["id"]!;
        var secondId = (string)_h.Handles[1]["id"]!;

        Assert.NotEqual(firstId, secondId);
        Reply(secondId, "\"two\"");

        Assert.Equal("\"two\"", await second.WaitAsync(Patience));
        Assert.False(first.IsCompleted);

        Reply(firstId, "\"one\"");
        Assert.Equal("\"one\"", await first.WaitAsync(Patience));
    }

    [Fact]
    public async Task A_request_the_engine_was_working_on_fails_when_a_new_page_starts()
    {
        Boot();
        Ready();
        var call = _h.Bridge.SendAsync("{}");

        Boot();

        AssertRestartError(await Answer(call));
    }

    [Fact]
    public async Task A_crash_of_the_page_fails_the_requests_at_once()
    {
        Boot();
        Ready();
        var call = _h.Bridge.SendAsync("{}");

        _h.Bridge.PageLost();

        AssertRestartError(await Answer(call));
    }

    [Fact]
    public async Task A_request_the_engine_never_received_survives_a_restart()
    {
        Boot();
        var call = _h.Bridge.SendAsync("""{"type":"arm"}""");

        _h.Bridge.PageLost();
        Boot();
        Assert.False(call.IsCompleted);

        Ready();
        var handle = Assert.Single(_h.Handles);
        Reply((string)handle["id"]!, """{"ok":true}""");
        Assert.Equal("""{"ok":true}""", await call.WaitAsync(Patience));
    }

    [Fact]
    public async Task The_answer_of_a_page_that_is_gone_is_ignored_and_ids_are_not_reused()
    {
        Boot();
        Ready();
        var lost = _h.Bridge.SendAsync("{}");
        var lostId = LastHandleId();
        Boot();
        AssertRestartError(await Answer(lost));

        Ready();
        Reply(lostId, """{"ok":true}""");
        var next = _h.Bridge.SendAsync("{}");

        Assert.False(next.IsCompleted);
        Assert.NotEqual(lostId, LastHandleId());
    }

    [Fact]
    public async Task A_request_cancelled_while_waiting_is_never_delivered()
    {
        Boot();
        using var cancel = new CancellationTokenSource();
        var call = _h.Bridge.SendAsync("{}", cancel.Token);

        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);

        Ready();
        Assert.Empty(_h.Handles);
    }

    [Fact]
    public async Task A_request_cancelled_after_delivery_ignores_the_late_answer()
    {
        Boot();
        Ready();
        using var cancel = new CancellationTokenSource();
        var call = _h.Bridge.SendAsync("{}", cancel.Token);
        var id = LastHandleId();

        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);

        Reply(id, "{}");
    }

    [Fact]
    public async Task A_request_whose_token_is_already_cancelled_is_refused()
    {
        Boot();
        Ready();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _h.Bridge.SendAsync("{}", new CancellationToken(canceled: true)));

        Assert.Empty(_h.Handles);
    }

    [Fact]
    public async Task An_answer_that_is_not_text_is_an_error_for_the_caller()
    {
        Boot();
        Ready();
        var call = _h.Bridge.SendAsync("{}");

        _h.Bridge.OnMessage(new JsonObject { ["t"] = "reply", ["id"] = LastHandleId(), ["json"] = 42 }.ToJsonString());

        var reply = await Answer(call);
        Assert.False((bool)reply["ok"]!);
    }

    [Fact]
    public void Settings_saved_by_the_engine_are_kept_and_not_sent_back()
    {
        Boot();

        _h.Bridge.OnMessage(Message("storageSet", ("json", """{"lastCategory":"films"}""")));

        Assert.Equal("films", _h.Config.GetString("lastCategory"));
        Assert.Empty(_h.Posted);
    }

    [Fact]
    public void Settings_saved_by_the_web_app_are_sent_to_the_engine()
    {
        Boot();

        _h.Config.Merge("""{"qbUrl":"http://q:8080"}""", SpaBridge.Origin);

        var message = Assert.Single(_h.Posted);
        Assert.Equal("config", (string?)message["t"]);
        Assert.Equal("http://q:8080", (string?)message["patch"]!["qbUrl"]);
    }

    [Fact]
    public void An_invalid_settings_patch_changes_nothing()
    {
        _h.Config.Merge("""{"qbUrl":"http://q:8080"}""", "test");

        _h.Bridge.OnMessage(Message("storageSet", ("json", "not json")));
        _h.Bridge.OnMessage("""{"t":"storageSet","json":42}""");

        Assert.Equal("http://q:8080", _h.Config.GetString("qbUrl"));
    }

    [Fact]
    public void Notifications_links_and_events_go_to_the_window()
    {
        _h.Bridge.OnMessage(Message("notify", ("title", "Téléchargement terminé"), ("text", "Un film")));
        _h.Bridge.OnMessage(Message("openUrl", ("url", "http://q:8080")));
        _h.Bridge.OnMessage(Message("emit", ("json", """{"type":"progress"}""")));

        string[] expected = ["notify:Téléchargement terminé|Un film", "openExternal:http://q:8080", """emit:{"type":"progress"}"""];
        Assert.Equal(expected, _h.Shell.Calls);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("42")]
    [InlineData("{}")]
    [InlineData("""{"t":42}""")]
    [InlineData("""{"t":"unknown"}""")]
    [InlineData("""{"t":"reply"}""")]
    [InlineData("""{"t":"reply","id":"nobody","json":"{}"}""")]
    [InlineData("""{"t":"openUrl"}""")]
    [InlineData("""{"t":"emit","json":42}""")]
    [InlineData("""{"t":"http"}""")]
    public void Messages_that_make_no_sense_are_ignored(string json)
    {
        _h.Bridge.OnMessage(json);

        Assert.Empty(_h.Shell.Calls);
        Assert.Empty(_h.Posted);
        Assert.Empty(_h.Network.Requests);
    }

    [Fact]
    public void A_misbehaving_window_does_not_break_the_bridge()
    {
        _h.Shell.Fault = new InvalidOperationException("boom");

        _h.Bridge.OnMessage(Message("notify", ("title", "t"), ("text", "x")));

        _h.Shell.Fault = null;
        _h.Bridge.OnMessage(Message("notify", ("title", "t2"), ("text", "x")));
        Assert.Equal("notify:t2|x", Assert.Single(_h.Shell.Calls));
    }

    [Fact]
    public async Task The_network_is_reached_through_the_window_and_the_answer_posted_back()
    {
        Boot();

        _h.Bridge.OnMessage(HttpCall("h1", "http://q.test:8080/api/v2/app/webapiVersion"));

        var done = await _h.WaitForAsync(HttpDone("h1"));
        Assert.Equal(200, (int)done["status"]!);
        Assert.Equal("Ok.", (string?)done["text"]);
        Assert.Null(done["error"]);
        var request = Assert.Single(_h.Network.Requests);
        Assert.Equal("http://q.test:8080/api/v2/app/webapiVersion", request.Target);
        Assert.Equal("http://q.test:8080", request.Header("Origin"));
    }

    [Fact]
    public async Task A_network_failure_comes_back_as_an_error()
    {
        using var h = new EngineHarness((_, _) => throw new HttpRequestException("refused"));

        h.Bridge.BuildState();
        h.Bridge.OnMessage(HttpCall("h1", "http://q.test:8080/"));

        var done = await h.WaitForAsync(HttpDone("h1"));
        Assert.False(string.IsNullOrEmpty((string?)done["error"]));
        Assert.Equal(0, (int)done["status"]!);
    }

    [Fact]
    public async Task The_answer_to_a_page_that_is_gone_is_not_delivered()
    {
        var release = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var h = new EngineHarness((_, _) => release.Task);
        h.Bridge.BuildState();
        h.Bridge.OnMessage(HttpCall("h1", "http://q.test:8080/"));
        await WaitUntilAsync(() => h.Network.Requests.Count == 1);

        h.Bridge.BuildState();
        release.SetResult(StubHandler.Reply(HttpStatusCode.OK, "late"u8.ToArray()));
        await Task.Delay(300);

        Assert.DoesNotContain(h.Posted, p => (string?)p["t"] == "httpDone");
    }

    [Fact]
    public async Task After_dispose_a_request_waiting_for_the_engine_gets_an_error()
    {
        Boot();
        var call = _h.Bridge.SendAsync("{}");

        _h.Bridge.Dispose();

        Assert.False((bool)(await Answer(call))["ok"]!);
    }

    [Fact]
    public async Task After_dispose_a_request_the_engine_is_working_on_gets_an_error()
    {
        Boot();
        Ready();
        var call = _h.Bridge.SendAsync("{}");

        _h.Bridge.Dispose();

        Assert.False((bool)(await Answer(call))["ok"]!);
    }

    [Fact]
    public async Task After_dispose_nothing_is_posted_and_nothing_reaches_the_window()
    {
        Boot();
        Ready();
        _h.Bridge.Dispose();

        Assert.False((bool)(await Answer(_h.Bridge.SendAsync("{}")))["ok"]!);
        Ready();
        _h.Config.Merge("""{"qbUrl":"http://q:8080"}""", SpaBridge.Origin);
        _h.Bridge.OnMessage(Message("notify", ("title", "t"), ("text", "x")));
        _h.Bridge.OnMessage(HttpCall("h1", "http://q.test:8080/"));
        await Task.Delay(200);

        Assert.Empty(_h.Posted);
        Assert.Empty(_h.Shell.Calls);
        Assert.Empty(_h.Network.Requests);
    }

    [Fact]
    public async Task After_dispose_a_network_answer_is_not_delivered()
    {
        using var h = new EngineHarness(async (_, cancellation) =>
        {
            await Task.Delay(Timeout.Infinite, cancellation);
            return StubHandler.Reply(HttpStatusCode.OK);
        });
        h.Bridge.BuildState();
        h.Bridge.OnMessage(HttpCall("h1", "http://q.test:8080/"));
        await WaitUntilAsync(() => h.Network.Requests.Count == 1);

        h.Bridge.Dispose();
        await Task.Delay(300);

        Assert.Empty(h.Posted);
    }

    [Fact]
    public void Dispose_can_be_called_twice()
    {
        _h.Bridge.Dispose();
        _h.Bridge.Dispose();
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = Environment.TickCount64 + 5000;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline) Assert.Fail("The condition was not met in time.");
            await Task.Delay(10);
        }
    }
}
