using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Arr411.Core.Bridge;
using Arr411.Core.Config;
using Arr411.Core.Http;
using Arr411.Core.Shell;

namespace Arr411.Core.Tests;

/// <summary>Records what the web app asked the window to do.</summary>
internal sealed class FakeWindow : ISpaShell
{
    private readonly List<string> _calls = [];
    private readonly List<DroppedContent> _drops = [];

    public string[] Calls
    {
        get
        {
            lock (_calls) return [.. _calls];
        }
    }

    /// <summary>When set, every call throws it (a window that misbehaves).</summary>
    public Exception? Fault { get; set; }

    public void SelectTab(string tab) => Add("selectTab:" + tab);

    public void OpenC411(string url) => Add("openC411:" + url);

    public void ReloadC411() => Add("reloadC411");

    public void OpenExternal(string url) => Add("openExternal:" + url);

    public void Toast(string text) => Add("toast:" + text);

    public void SetBadge(int count) => Add("badge:" + count);

    public void CopyText(string text) => Add("copy:" + text);

    public void Dropped(DroppedContent content)
    {
        Add("dropped");
        lock (_calls) _drops.Add(content);
    }

    /// <summary>What the web app said was dropped on it, in order.</summary>
    public DroppedContent[] Drops
    {
        get
        {
            lock (_calls) return [.. _drops];
        }
    }

    private void Add(string call)
    {
        if (Fault is not null) throw Fault;
        lock (_calls) _calls.Add(call);
    }
}

/// <summary>A bridge wired to fakes: a stub network, a throw-away settings file, a recording window and a collector for what it posts.</summary>
internal sealed class BridgeHarness : IDisposable
{
    public const string Version = "1.2.3";

    private readonly TempDir _dir = new();
    private readonly NativeHttp _net;
    private readonly List<JsonObject> _posted = [];

    public BridgeHarness(Func<RecordedRequest, CancellationToken, Task<HttpResponseMessage>>? network = null)
    {
        Config = new ConfigStore(_dir.File("config.dat"), new ReversibleProtector());
        Network = new StubHandler(network ?? ((_, _) => Task.FromResult(StubHandler.Reply(HttpStatusCode.OK, "ok"u8.ToArray()))));
        _net = new NativeHttp("UA", Network);
        Bridge = new SpaBridge(Config, _net, Window, State, Collect, Version);
    }

    public ConfigStore Config { get; }

    public StubHandler Network { get; }

    public FakeWindow Window { get; } = new();

    public TabState State { get; } = new();

    public SpaBridge Bridge { get; }

    public JsonObject[] Posted
    {
        get
        {
            lock (_posted) return [.. _posted];
        }
    }

    public string[] Types => [.. Posted.Select(p => (string)p["t"]!)];

    public void ClearPosted()
    {
        lock (_posted) _posted.Clear();
    }

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

    public async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline) Assert.Fail("The condition was not met in time.");
            await Task.Delay(10);
        }
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

public sealed class SpaBridgeTests : IDisposable
{
    private readonly BridgeHarness _h = new();

    public void Dispose() => _h.Dispose();

    private static string Save(string patch) => new JsonObject { ["t"] = "saveConfig", ["patch"] = patch }.ToJsonString();

    private static string HttpCall(string id, string request) => new JsonObject { ["t"] = "http", ["id"] = id, ["req"] = request }.ToJsonString();

    private static string Reply(string id, JsonNode? value) => new JsonObject { ["t"] = "reply", ["id"] = id, ["value"] = value }.ToJsonString();

    private static Func<JsonObject, bool> HttpDone(string id) => p => (string?)p["t"] == "httpDone" && (string?)p["id"] == id;

    /// <summary>The window is on <paramref name="tab"/> and a page of the web app has just asked for its state.</summary>
    private void Boot(string tab = Tabs.C411)
    {
        _h.State.Show(tab, tellSpa: true);
        _h.Bridge.BuildState();
    }

    private void Ready() => _h.Bridge.OnMessage("""{"t":"ready"}""");

    private string PendingBackId() => (string)_h.Posted.Last(p => (string?)p["t"] == "back")["id"]!;

    [Fact]
    public void The_boot_state_carries_the_settings_the_tab_and_the_version()
    {
        _h.Config.Merge("""{"radarrUrl":"http://r:7878","title":"été"}""", "test");
        _h.State.Show(Tabs.Radarr, tellSpa: true);

        var state = JsonNode.Parse(_h.Bridge.BuildState())!.AsObject();

        Assert.Equal(Tabs.Radarr, (string?)state["tab"]);
        Assert.Equal(BridgeHarness.Version, (string?)state["version"]);
        var config = (string)state["config"]!;
        Assert.Contains("été", config);
        Assert.Equal("http://r:7878", (string?)JsonNode.Parse(config)!["radarrUrl"]);
    }

    [Fact]
    public void Loading_a_page_makes_the_bridge_wait_for_its_ready_signal()
    {
        Boot();
        Ready();
        Assert.True(_h.State.SpaReady);

        _h.Bridge.BuildState();

        Assert.False(_h.State.SpaReady);
    }

    [Fact]
    public void Ready_after_a_normal_boot_only_reports_the_foreground_state()
    {
        Boot(Tabs.Sonarr);

        Ready();

        Assert.Equal(["foreground"], _h.Types);
        Assert.True((bool)_h.Posted[0]["on"]!);
    }

    [Fact]
    public void Ready_tells_the_page_to_switch_when_the_tab_changed_while_it_was_booting()
    {
        Boot(Tabs.Radarr);
        _h.State.Show(Tabs.Sonarr, tellSpa: true);

        Ready();

        Assert.Equal(["showTab", "foreground"], _h.Types);
        Assert.Equal(Tabs.Sonarr, (string?)_h.Posted[0]["tab"]);
    }

    [Fact]
    public void Ready_without_a_boot_state_still_syncs_the_tab()
    {
        _h.State.Show(Tabs.Qbit, tellSpa: true);

        Ready();

        Assert.Equal(["showTab", "foreground"], _h.Types);
        Assert.Equal(Tabs.Qbit, (string?)_h.Posted[0]["tab"]);
    }

    [Fact]
    public void Shares_wait_for_the_page_and_arrive_in_order()
    {
        Boot();
        _h.Bridge.Share(new JsonObject { ["text"] = "magnet:?xt=urn:btih:1" });
        _h.Bridge.Share(new JsonObject { ["text"] = "magnet:?xt=urn:btih:2" });
        Assert.Empty(_h.Posted);

        Ready();

        Assert.Equal(["foreground", "shared", "shared"], _h.Types);
        Assert.Equal("magnet:?xt=urn:btih:1", (string?)_h.Posted[1]["data"]!["text"]);
        Assert.Equal("magnet:?xt=urn:btih:2", (string?)_h.Posted[2]["data"]!["text"]);
    }

    [Fact]
    public void A_queued_share_is_a_snapshot()
    {
        Boot();
        var data = new JsonObject { ["text"] = "before" };
        _h.Bridge.Share(data);
        data["text"] = "after";

        Ready();

        Assert.Equal("before", (string?)_h.Posted.Single(p => (string?)p["t"] == "shared")["data"]!["text"]);
    }

    [Fact]
    public void A_share_is_delivered_at_once_when_the_page_is_ready()
    {
        Boot();
        Ready();
        _h.ClearPosted();

        _h.Bridge.Share(new JsonObject { ["tab"] = "settings", ["section"] = "c411" });

        var shared = Assert.Single(_h.Posted);
        Assert.Equal("shared", (string?)shared["t"]);
        Assert.Equal("settings", (string?)shared["data"]!["tab"]);
        Assert.Equal("c411", (string?)shared["data"]!["section"]);
    }

    [Fact]
    public void A_reloaded_page_gets_the_shares_made_while_it_was_loading()
    {
        Boot();
        Ready();
        _h.Bridge.BuildState();
        _h.ClearPosted();

        _h.Bridge.Share(new JsonObject { ["text"] = "m" });
        Assert.Empty(_h.Posted);
        Ready();

        Assert.Equal(["foreground", "shared"], _h.Types);
    }

    [Fact]
    public void The_foreground_state_is_remembered_until_the_page_is_ready_then_follows_every_change()
    {
        Boot();
        _h.Bridge.SetForeground(false);
        Assert.Empty(_h.Posted);

        Ready();
        Assert.False((bool)_h.Posted[^1]["on"]!);

        _h.Bridge.SetForeground(true);
        Assert.Equal("foreground", (string?)_h.Posted[^1]["t"]);
        Assert.True((bool)_h.Posted[^1]["on"]!);
    }

    [Fact]
    public void Reselecting_a_tab_is_forwarded_only_while_the_page_is_ready()
    {
        Boot();
        _h.Bridge.ReselectTab(Tabs.Radarr);
        Assert.Empty(_h.Posted);

        Ready();
        _h.ClearPosted();
        _h.Bridge.ReselectTab(Tabs.Radarr);
        var message = Assert.Single(_h.Posted);
        Assert.Equal("tabReselect", (string?)message["t"]);
        Assert.Equal(Tabs.Radarr, (string?)message["tab"]);

        _h.Bridge.BuildState();
        _h.ClearPosted();
        _h.Bridge.ReselectTab(Tabs.Radarr);
        Assert.Empty(_h.Posted);
    }

    [Fact]
    public void The_window_can_switch_the_page_to_a_tab_and_have_it_read_its_settings_again()
    {
        _h.Bridge.ShowTab(Tabs.Lidarr);
        _h.Bridge.ReloadConfig();

        Assert.Equal(["showTab", "loadCfg"], _h.Types);
        Assert.Equal(Tabs.Lidarr, (string?)_h.Posted[0]["tab"]);
    }

    [Fact]
    public void Settings_saved_by_the_page_are_stored_and_not_sent_back()
    {
        _h.Bridge.OnMessage(Save("""{"radarrUrl":"http://r:7878","radarrKey":"k"}"""));

        Assert.Equal("http://r:7878", _h.Config.GetString("radarrUrl"));
        Assert.Equal("k", _h.Config.GetString("radarrKey"));
        Assert.Empty(_h.Posted);
    }

    [Fact]
    public void The_tracking_list_of_the_page_is_never_stored()
    {
        _h.Bridge.OnMessage(Save("""{"tracked":{"abc":{"n":1}},"radarrUrl":"http://r"}"""));

        Assert.Equal("http://r", _h.Config.GetString("radarrUrl"));
        Assert.DoesNotContain("tracked", _h.Config.ToJson());
    }

    [Fact]
    public void A_stale_tracking_list_from_the_page_does_not_overwrite_the_engine_one()
    {
        _h.Config.Merge("""{"tracked":{"abc":{"n":1}}}""", "engine");

        _h.Bridge.OnMessage(Save("""{"tracked":{},"radarrUrl":"http://r"}"""));

        Assert.Contains("\"abc\":{\"n\":1}", _h.Config.ToJson());
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1,2,3]")]
    [InlineData("42")]
    [InlineData("null")]
    [InlineData("\"text\"")]
    [InlineData("")]
    public void Invalid_patches_are_ignored(string patch)
    {
        _h.Config.Merge("""{"a":"1"}""", "test");
        _h.ClearPosted();
        var before = _h.Config.ToJson();

        _h.Bridge.OnMessage(Save(patch));

        Assert.Equal(before, _h.Config.ToJson());
        Assert.Empty(_h.Posted);
    }

    [Fact]
    public void A_save_without_a_patch_is_ignored()
    {
        _h.Bridge.OnMessage("""{"t":"saveConfig"}""");

        Assert.Equal("{}", _h.Config.ToJson());
    }

    [Fact]
    public void Settings_changed_elsewhere_are_pushed_to_the_page()
    {
        _h.Config.Merge("""{"qbitUrl":"http://q:8080"}""", "engine");

        var message = Assert.Single(_h.Posted);
        Assert.Equal("config", (string?)message["t"]);
        Assert.Equal("http://q:8080", (string?)message["patch"]!["qbitUrl"]);
    }

    [Fact]
    public void Only_the_keys_that_changed_are_pushed()
    {
        _h.Config.Merge("""{"a":"1"}""", "engine");
        _h.ClearPosted();

        _h.Config.Merge("""{"a":"1","b":"2"}""", "engine");

        var patch = Assert.Single(_h.Posted)["patch"]!.AsObject();
        Assert.Equal(["b"], patch.Select(p => p.Key).ToArray());
    }

    [Fact]
    public async Task A_request_is_answered_with_a_single_message()
    {
        using var h = new BridgeHarness((_, _) => Task.FromResult(StubHandler.Reply(HttpStatusCode.OK, "héllo"u8.ToArray())));

        h.Bridge.OnMessage(HttpCall("a1", """{"url":"http://svc.test/api"}"""));
        var done = await h.WaitForAsync(HttpDone("a1"));

        Assert.Equal(200, (int)done["status"]!);
        Assert.Null(done["error"]);
        Assert.Equal("héllo", (string?)done["body"]);
        Assert.Equal("http://svc.test/api", Assert.Single(h.Network.Requests).Target);
    }

    [Fact]
    public async Task A_network_failure_is_reported_with_status_zero_and_a_message()
    {
        using var h = new BridgeHarness((_, _) =>
            throw new HttpRequestException(HttpRequestError.ConnectionError, "refused", new SocketException((int)SocketError.ConnectionRefused)));

        h.Bridge.OnMessage(HttpCall("f1", """{"url":"http://svc.test/api"}"""));
        var done = await h.WaitForAsync(HttpDone("f1"));

        Assert.Equal(0, (int)done["status"]!);
        Assert.StartsWith("Réseau : connexion refusée (", (string?)done["error"]);
        Assert.Equal("", (string?)done["body"]);
    }

    [Fact]
    public async Task A_request_without_a_body_is_answered_with_an_error()
    {
        _h.Bridge.OnMessage("""{"t":"http","id":"x"}""");

        var done = await _h.WaitForAsync(HttpDone("x"));

        Assert.Equal(0, (int)done["status"]!);
        Assert.StartsWith("Réseau : ", (string?)done["error"]);
    }

    [Fact]
    public async Task A_request_without_an_id_is_ignored()
    {
        _h.Bridge.OnMessage("""{"t":"http","req":"{\"url\":\"http://svc.test/\"}"}""");
        await Task.Delay(150);

        Assert.Empty(_h.Network.Requests);
        Assert.Empty(_h.Posted);
    }

    [Fact]
    public async Task The_answer_for_a_page_that_was_reloaded_is_dropped()
    {
        var release = new TaskCompletionSource();
        using var h = new BridgeHarness(async (request, _) =>
        {
            if (request.Target.EndsWith("/slow", StringComparison.Ordinal)) await release.Task;
            return StubHandler.Reply(HttpStatusCode.OK, "late"u8.ToArray());
        });

        h.Bridge.OnMessage(HttpCall("old", """{"url":"http://svc.test/slow"}"""));
        await h.WaitUntilAsync(() => h.Network.Requests.Count == 1);
        h.Bridge.BuildState();
        release.SetResult();
        await Task.Delay(300);
        Assert.DoesNotContain("httpDone", h.Types);

        h.Bridge.OnMessage(HttpCall("new", """{"url":"http://svc.test/fast"}"""));
        await h.WaitForAsync(HttpDone("new"));
        Assert.DoesNotContain(h.Posted, p => (string?)p["id"] == "old");
    }

    [Theory]
    [InlineData("""{"t":"selectTab","tab":"radarr"}""", "selectTab:radarr")]
    [InlineData("""{"t":"selectTab","tab":"settings"}""", "selectTab:settings")]
    [InlineData("""{"t":"openC411","url":"https://c411.org/torrents"}""", "openC411:https://c411.org/torrents")]
    [InlineData("""{"t":"reloadC411"}""", "reloadC411")]
    [InlineData("""{"t":"openUrl","url":"https://example.org/a?b=1"}""", "openExternal:https://example.org/a?b=1")]
    [InlineData("""{"t":"openUrl","url":"magnet:?xt=urn:btih:abc"}""", "openExternal:magnet:?xt=urn:btih:abc")]
    [InlineData("""{"t":"toast","text":"Ajouté ✓"}""", "toast:Ajouté ✓")]
    [InlineData("""{"t":"setBadge","n":3}""", "badge:3")]
    [InlineData("""{"t":"setBadge","n":0}""", "badge:0")]
    [InlineData("""{"t":"setBadge","n":-4}""", "badge:0")]
    [InlineData("""{"t":"setBadge","n":"many"}""", "badge:0")]
    [InlineData("""{"t":"setBadge"}""", "badge:0")]
    [InlineData("""{"t":"copy","text":"magnet:?xt=urn:btih:abc"}""", "copy:magnet:?xt=urn:btih:abc")]
    public void The_calls_of_the_page_reach_the_window(string message, string expected)
    {
        _h.Bridge.OnMessage(message);

        Assert.Equal([expected], _h.Window.Calls);
    }

    [Theory]
    [InlineData("""{"t":"selectTab","tab":"nope"}""")]
    [InlineData("""{"t":"selectTab","tab":""}""")]
    [InlineData("""{"t":"selectTab"}""")]
    [InlineData("""{"t":"selectTab","tab":5}""")]
    [InlineData("""{"t":"openC411"}""")]
    [InlineData("""{"t":"openUrl","url":null}""")]
    [InlineData("""{"t":"toast","text":7}""")]
    [InlineData("""{"t":"copy"}""")]
    [InlineData("""{"t":"haptic"}""")]
    [InlineData("""{"t":"unknown"}""")]
    [InlineData("""{"t":5}""")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("42")]
    [InlineData("null")]
    [InlineData("not json")]
    [InlineData("")]
    public void Malformed_or_unknown_messages_are_ignored(string message)
    {
        _h.Bridge.OnMessage(message);

        Assert.Empty(_h.Window.Calls);
        Assert.Empty(_h.Posted);
    }

    [Fact]
    public void A_window_that_fails_does_not_break_the_bridge()
    {
        _h.Window.Fault = new InvalidOperationException("boom");
        _h.Bridge.OnMessage("""{"t":"toast","text":"lost"}""");
        _h.Window.Fault = null;

        _h.Bridge.OnMessage("""{"t":"toast","text":"kept"}""");

        Assert.Equal(["toast:kept"], _h.Window.Calls);
    }

    [Fact]
    public void What_is_dropped_on_the_web_app_reaches_the_window_checked()
    {
        var message = new JsonObject
        {
            ["t"] = "drop",
            ["text"] = "magnet:?xt=urn:btih:abc https://c411.org/torrents/1",
            ["files"] = new JsonArray(
                new JsonObject { ["name"] = "film.torrent", ["base64"] = Convert.ToBase64String("d4:spam4:eggse"u8) },
                new JsonObject { ["name"] = "photo.png" }),
        };

        _h.Bridge.OnMessage(message.ToJsonString());

        var drop = Assert.Single(_h.Window.Drops);
        Assert.Equal(["magnet:?xt=urn:btih:abc"], drop.Request.Magnets);
        Assert.Equal("https://c411.org/torrents/1", drop.Request.C411Url);
        Assert.Equal(["film.torrent"], drop.Files.Select(f => f.Name).ToArray());
        Assert.Equal(["« photo.png » n’est pas un fichier .torrent"], drop.Problems);
    }

    [Fact]
    public void A_drop_is_not_a_command_for_the_window_beyond_what_it_holds()
    {
        _h.Bridge.OnMessage("""{"t":"drop","text":"--quit --tab=qbit"}""");

        var drop = Assert.Single(_h.Window.Drops);
        Assert.False(drop.HasWork);
        Assert.False(drop.Request.Quit);
        Assert.Null(drop.Request.Tab);
    }

    [Fact]
    public void A_window_that_fails_on_a_drop_does_not_break_the_bridge()
    {
        _h.Window.Fault = new InvalidOperationException("boom");
        _h.Bridge.OnMessage("""{"t":"drop","text":"magnet:?xt=urn:btih:abc"}""");
        _h.Window.Fault = null;

        _h.Bridge.OnMessage("""{"t":"drop","text":"magnet:?xt=urn:btih:def"}""");

        Assert.Equal(["magnet:?xt=urn:btih:def"], Assert.Single(_h.Window.Drops).Request.Magnets);
    }

    [Fact]
    public async Task Back_returns_what_the_page_answers()
    {
        foreach (var answer in new[] { true, false })
        {
            var pending = _h.Bridge.BackAsync();

            _h.Bridge.OnMessage(Reply(PendingBackId(), answer));

            Assert.Equal(answer, await pending);
        }
    }

    [Fact]
    public async Task Back_gives_up_when_the_page_does_not_answer()
    {
        var watch = Stopwatch.StartNew();

        Assert.False(await _h.Bridge.BackAsync());

        Assert.InRange(watch.ElapsedMilliseconds, 600, 5000);
    }

    [Fact]
    public async Task Back_is_answered_no_at_once_when_the_page_reloads()
    {
        var pending = _h.Bridge.BackAsync();

        _h.Bridge.BuildState();

        Assert.False(await pending.WaitAsync(TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public async Task A_reply_nobody_waits_for_is_ignored()
    {
        _h.Bridge.OnMessage(Reply("b999", true));
        var pending = _h.Bridge.BackAsync();

        _h.Bridge.OnMessage(Reply("other", true));
        Assert.False(pending.IsCompleted);

        _h.Bridge.OnMessage(Reply(PendingBackId(), true));
        Assert.True(await pending);
    }

    [Fact]
    public async Task Only_a_true_reply_means_yes()
    {
        foreach (var value in new JsonNode?[] { "yes", 1, null })
        {
            var pending = _h.Bridge.BackAsync();

            _h.Bridge.OnMessage(Reply(PendingBackId(), value));

            Assert.False(await pending);
        }
    }

    [Fact]
    public async Task A_disposed_bridge_goes_quiet()
    {
        _h.Bridge.Dispose();

        _h.Config.Merge("""{"a":"1"}""", "engine");
        _h.Bridge.ShowTab(Tabs.Radarr);
        _h.Bridge.ReloadConfig();
        _h.Bridge.OnMessage("""{"t":"toast","text":"x"}""");

        Assert.Empty(_h.Posted);
        Assert.Empty(_h.Window.Calls);
        Assert.False(await _h.Bridge.BackAsync());
    }

    [Fact]
    public void Dispose_can_be_called_more_than_once()
    {
        _h.Bridge.Dispose();
        _h.Bridge.Dispose();
    }
}
