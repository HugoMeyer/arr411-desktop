using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Arr411.Core.Bridge;
using Arr411.Core.Files;
using Arr411.Core.Http;
using Arr411.Core.Shell;

namespace Arr411.Core.Tests;

/// <summary>An engine that answers what the test tells it to, and remembers what it was asked.</summary>
internal sealed class FakeEngineClient : IEngineClient
{
    private readonly List<string> _sent = [];

    /// <summary>What the engine answers; a plain success unless the test says otherwise.</summary>
    public Func<string, CancellationToken, Task<string>> Answer { get; set; } = (_, _) => Task.FromResult("""{"ok":true}""");

    public string[] Sent
    {
        get
        {
            lock (_sent) return [.. _sent];
        }
    }

    public Task<string> SendAsync(string messageJson, CancellationToken cancellation = default)
    {
        lock (_sent) _sent.Add(messageJson);
        return Answer(messageJson, cancellation);
    }
}

/// <summary>The window as the bridge of the C411 page sees it.</summary>
internal sealed class FakePageShell : IPageShell
{
    private readonly List<string> _toasts = [];
    private readonly List<DroppedContent> _drops = [];

    public string UserAgent { get; set; } = "TestAgent/1.0";

    public Func<Uri, Task<string?>> Cookies { get; set; } = _ => Task.FromResult<string?>(null);

    public string[] Toasts
    {
        get
        {
            lock (_toasts) return [.. _toasts];
        }
    }

    public void Toast(string text)
    {
        lock (_toasts) _toasts.Add(text);
    }

    public Task<string?> CookieHeaderAsync(Uri url) => Cookies(url);

    public void Dropped(DroppedContent content)
    {
        lock (_toasts) _drops.Add(content);
    }

    /// <summary>What the page said was dropped on it, in order.</summary>
    public DroppedContent[] Drops
    {
        get
        {
            lock (_toasts) return [.. _drops];
        }
    }
}

/// <summary>Collection expressions do not pick an <c>Assert.Equal</c> overload when the other side is an array: these helpers type them.</summary>
internal static class Expect
{
    public static void Equal(string[] expected, string[] actual) => Assert.Equal(expected, actual);

    public static void Equal(byte[] expected, byte[] actual) => Assert.Equal(expected, actual);
}

/// <summary>The messages the C411 page posts (see Scripts\page-adapter.js) and how to recognise what the bridge posts back.</summary>
internal static class PageMessages
{
    public static string Send(string? id, JsonObject message) =>
        new JsonObject { ["t"] = "send", ["id"] = id, ["msg"] = message.ToJsonString() }.ToJsonString();

    public static string Download(string url, string filename) =>
        new JsonObject { ["t"] = "download", ["url"] = url, ["filename"] = filename }.ToJsonString();

    public static string SaveFile(string name, byte[] data) =>
        new JsonObject { ["t"] = "saveFile", ["name"] = name, ["base64"] = Convert.ToBase64String(data), ["mime"] = "application/octet-stream" }.ToJsonString();

    public static string Drop(string? text = null, params (string Name, string Base64)[] files)
    {
        var message = new JsonObject { ["t"] = "drop" };
        if (text is not null) message["text"] = text;

        var entries = new JsonArray();
        foreach (var (name, base64) in files) entries.Add(new JsonObject { ["name"] = name, ["base64"] = base64 });
        message["files"] = entries;
        return message.ToJsonString();
    }

    public static string FetchBytes(string id, string url, string? referer = null) =>
        new JsonObject { ["t"] = "fetchBytes", ["id"] = id, ["url"] = url, ["referer"] = referer }.ToJsonString();

    public static Func<JsonObject, bool> Reply(string id) => p => (string?)p["t"] == "reply" && (string?)p["id"] == id;
}

/// <summary>A page bridge wired to fakes: an engine and a window that record, a stub network, a throw-away Downloads folder.</summary>
internal sealed class PageHarness : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly List<JsonObject> _posted = [];

    public PageHarness(
        Func<RecordedRequest, CancellationToken, Task<HttpResponseMessage>>? network = null,
        TimeSpan? engineTimeout = null,
        TimeSpan? offScreenGrace = null,
        TimeSpan? rateWindow = null)
    {
        Network = new StubHandler(network ?? ((_, _) => Task.FromResult(StubHandler.Reply(HttpStatusCode.OK, "data"u8.ToArray()))));
        Fetcher = new PageFetcher(Network);
        Downloads = _dir.File("Downloads");
        Bridge = new PageBridge(Engine, Shell, Fetcher, Downloads, Collect)
        {
            EngineTimeout = engineTimeout ?? PageBridge.DefaultEngineTimeout,
            OffScreenGrace = offScreenGrace ?? PageBridge.DefaultOffScreenGrace,
            RateWindow = rateWindow ?? PageBridge.DefaultRateWindow,
        };
    }

    public FakeEngineClient Engine { get; } = new();

    public FakePageShell Shell { get; } = new();

    public StubHandler Network { get; }

    public PageFetcher Fetcher { get; }

    public PageBridge Bridge { get; }

    /// <summary>Not created until something is saved in it.</summary>
    public string Downloads { get; }

    public JsonObject[] Posted
    {
        get
        {
            lock (_posted) return [.. _posted];
        }
    }

    /// <summary>The names of the files in the Downloads folder.</summary>
    public string[] Files => Directory.Exists(Downloads) ? [.. new DirectoryInfo(Downloads).GetFiles().Select(f => f.Name).Order()] : [];

    public void Page(string json) => Bridge.OnMessage(json);

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

    public async Task WaitForToastAsync(string text, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (Shell.Toasts.Contains(text)) return;
            await Task.Delay(10);
        }

        Assert.Fail("No toast \"" + text + "\". Toasts: " + string.Join(" | ", Shell.Toasts));
    }

    public void Dispose()
    {
        Bridge.Dispose();
        Fetcher.Dispose();
        Network.Dispose();
        _dir.Dispose();
    }

    private void Collect(string json)
    {
        var message = JsonNode.Parse(json)!.AsObject();
        lock (_posted) _posted.Add(message);
    }
}

public sealed class PageBridgeTests : IDisposable
{
    private const string TooManyFetches = """{"ok":false,"error":"Trop de téléchargements en cours"}""";

    private readonly PageHarness _h = new();

    public void Dispose() => _h.Dispose();

    private static JsonObject Status() => new() { ["type"] = "pageStatus" };

    [Theory]
    [InlineData("getCategories")]
    [InlineData("pageStatus")]
    [InlineData("qbitIndex")]
    [InlineData("arm")]
    [InlineData("disarm")]
    [InlineData("upload")]
    [InlineData("arrMeta")]
    public async Task The_messages_of_the_list_reach_the_engine_and_its_answer_comes_back(string type)
    {
        _h.Engine.Answer = (_, _) => Task.FromResult("""{"ok":true,"n":1}""");

        _h.Page(PageMessages.Send("a1", new JsonObject { ["type"] = type, ["hash"] = "abc" }));

        var reply = await _h.WaitForAsync(PageMessages.Reply("a1"));
        Assert.Equal("""{"ok":true,"n":1}""", (string?)reply["json"]);
        var sent = JsonNode.Parse(Assert.Single(_h.Engine.Sent))!;
        Assert.Equal(type, (string?)sent["type"]);
        Assert.Equal("abc", (string?)sent["hash"]);
    }

    [Theory]
    [InlineData("saveConfig")]
    [InlineData("listTorrents")]
    [InlineData("http")]
    [InlineData("__download")]
    [InlineData("Arm")]
    [InlineData("")]
    public async Task Other_messages_are_refused_without_troubling_the_engine(string type)
    {
        _h.Page(PageMessages.Send("a1", new JsonObject { ["type"] = type }));

        var reply = await _h.WaitForAsync(PageMessages.Reply("a1"));
        Assert.Equal("""{"ok":false,"error":"Message refusé"}""", (string?)reply["json"]);
        Assert.Empty(_h.Engine.Sent);
    }

    [Theory]
    [InlineData("""{"foo":1}""")]
    [InlineData("""{"type":5}""")]
    [InlineData("""{"type":null}""")]
    [InlineData("""[{"type":"arm"}]""")]
    [InlineData("\"arm\"")]
    [InlineData("null")]
    [InlineData("42")]
    [InlineData("not json")]
    [InlineData("")]
    public async Task Anything_that_is_not_a_typed_object_is_refused(string message)
    {
        _h.Page(new JsonObject { ["t"] = "send", ["id"] = "a1", ["msg"] = message }.ToJsonString());

        var reply = await _h.WaitForAsync(PageMessages.Reply("a1"));
        Assert.Contains("Message refusé", (string?)reply["json"]);
        Assert.Empty(_h.Engine.Sent);
    }

    [Theory]
    [InlineData("""{"t":"send","id":"a1"}""")]
    [InlineData("""{"t":"send","id":"a1","msg":{"type":"arm"}}""")]
    [InlineData("""{"t":"send","id":"a1","msg":7}""")]
    public async Task A_message_that_is_not_text_is_refused(string json)
    {
        _h.Page(json);

        var reply = await _h.WaitForAsync(PageMessages.Reply("a1"));
        Assert.Contains("Message refusé", (string?)reply["json"]);
        Assert.Empty(_h.Engine.Sent);
    }

    [Fact]
    public async Task The_settings_a_page_slips_into_a_message_are_dropped()
    {
        _h.Page(PageMessages.Send("a1", new JsonObject
        {
            ["type"] = "arrMeta",
            ["cfg"] = new JsonObject { ["radarrUrl"] = "http://evil.example" },
            ["title"] = "Dune",
        }));

        await _h.WaitForAsync(PageMessages.Reply("a1"));
        var sent = JsonNode.Parse(Assert.Single(_h.Engine.Sent))!.AsObject();
        Assert.False(sent.ContainsKey("cfg"));
        Assert.Equal("arrMeta", (string?)sent["type"]);
        Assert.Equal("Dune", (string?)sent["title"]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void A_call_without_an_id_is_ignored(string? id)
    {
        _h.Page(PageMessages.Send(id, Status()));

        Assert.Empty(_h.Engine.Sent);
        Assert.Empty(_h.Posted);
    }

    [Fact]
    public void An_id_that_is_not_text_is_ignored()
    {
        _h.Page("""{"t":"send","id":7,"msg":"{\"type\":\"pageStatus\"}"}""");

        Assert.Empty(_h.Engine.Sent);
        Assert.Empty(_h.Posted);
    }

    [Fact]
    public async Task An_id_may_be_up_to_the_limit_and_no_longer()
    {
        var longest = new string('x', PageBridge.MaxIdLength);

        _h.Page(PageMessages.Send(longest + "y", Status()));
        Assert.Empty(_h.Engine.Sent);
        Assert.Empty(_h.Posted);

        _h.Page(PageMessages.Send(longest, Status()));
        await _h.WaitForAsync(PageMessages.Reply(longest));
    }

    [Fact]
    public async Task Requests_are_answered_independently_and_in_any_order()
    {
        var gates = new Dictionary<string, TaskCompletionSource<string>>();
        _h.Engine.Answer = (message, _) =>
        {
            var source = new TaskCompletionSource<string>();
            lock (gates) gates[(string)JsonNode.Parse(message)!["n"]!] = source;
            return source.Task;
        };

        _h.Page(PageMessages.Send("first", new JsonObject { ["type"] = "pageStatus", ["n"] = "1" }));
        _h.Page(PageMessages.Send("second", new JsonObject { ["type"] = "pageStatus", ["n"] = "2" }));

        gates["2"].SetResult("\"two\"");
        Assert.Equal("\"two\"", (string?)(await _h.WaitForAsync(PageMessages.Reply("second")))["json"]);
        Assert.DoesNotContain(_h.Posted, p => PageMessages.Reply("first")(p));

        gates["1"].SetResult("\"one\"");
        Assert.Equal("\"one\"", (string?)(await _h.WaitForAsync(PageMessages.Reply("first")))["json"]);
    }

    [Fact]
    public async Task An_answer_for_a_page_that_is_gone_is_dropped()
    {
        var gate = new TaskCompletionSource<string>();
        _h.Engine.Answer = (_, _) => gate.Task;
        _h.Page(PageMessages.Send("a1", Status()));

        _h.Bridge.NewPage();
        gate.SetResult("""{"ok":"stale"}""");

        // The same id on the new document gets its own answer, and only that one.
        _h.Engine.Answer = (_, _) => Task.FromResult("""{"ok":"fresh"}""");
        _h.Page(PageMessages.Send("a1", Status()));

        var reply = await _h.WaitForAsync(PageMessages.Reply("a1"));
        Assert.Equal("""{"ok":"fresh"}""", (string?)reply["json"]);
        Assert.Single(_h.Posted);
    }

    [Fact]
    public async Task An_engine_that_fails_gives_an_error_answer_and_the_bridge_goes_on()
    {
        _h.Engine.Answer = (_, _) => throw new InvalidOperationException("boom");
        _h.Page(PageMessages.Send("a1", Status()));
        var reply = await _h.WaitForAsync(PageMessages.Reply("a1"));
        Assert.Equal("""{"ok":false,"error":"Erreur interne"}""", (string?)reply["json"]);

        _h.Engine.Answer = (_, _) => Task.FromResult("""{"ok":true}""");
        _h.Page(PageMessages.Send("a2", Status()));
        await _h.WaitForAsync(PageMessages.Reply("a2"));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("""{"t":"nobody"}""")]
    [InlineData("""{"t":5}""")]
    [InlineData("""{"id":"a1"}""")]
    public void A_message_that_means_nothing_does_nothing(string json)
    {
        _h.Page(json);

        Assert.Empty(_h.Engine.Sent);
        Assert.Empty(_h.Posted);
        Assert.Empty(_h.Shell.Toasts);
    }

    [Fact]
    public void An_event_of_the_engine_is_posted_to_the_page()
    {
        _h.Bridge.Emit("""{"type":"fetchTorrent","url":"https://c411.org/a"}""");

        var posted = Assert.Single(_h.Posted);
        Assert.Equal("event", (string?)posted["t"]);
        Assert.Equal("""{"type":"fetchTorrent","url":"https://c411.org/a"}""", (string?)posted["json"]);
    }

    [Fact]
    public async Task A_disposed_bridge_cancels_what_waits_and_stays_silent()
    {
        var seen = CancellationToken.None;
        var gate = new TaskCompletionSource<string>();
        _h.Engine.Answer = (_, token) =>
        {
            seen = token;
            return gate.Task.WaitAsync(token);
        };
        _h.Page(PageMessages.Send("a1", Status()));
        Assert.False(seen.IsCancellationRequested);

        _h.Bridge.Dispose();
        _h.Bridge.Dispose();

        Assert.True(seen.IsCancellationRequested);
        _h.Page(PageMessages.Send("a2", Status()));
        _h.Bridge.Emit("{}");
        _h.Page("""{"t":"toast","text":"hello"}""");
        Assert.Null(await _h.Bridge.PlanDownloadAsync("https://c411.org/a", "a.torrent"));
        Assert.Single(_h.Engine.Sent);
        Assert.Empty(_h.Posted);
        Assert.Empty(_h.Shell.Toasts);
        Assert.Empty(_h.Files);
    }

    [Fact]
    public void A_toast_asked_by_the_page_is_shown_and_kept_short()
    {
        _h.Page("""{"t":"toast","text":"Envoyé à qBittorrent"}""");
        _h.Page(new JsonObject { ["t"] = "toast", ["text"] = new string('x', 500) }.ToJsonString());
        _h.Page("""{"t":"toast"}""");
        _h.Page("""{"t":"toast","text":5}""");

        string[] expected = ["Envoyé à qBittorrent", new string('x', 200)];
        Assert.Equal(expected, _h.Shell.Toasts);
    }

    [Fact]
    public async Task A_download_the_engine_takes_over_shows_nothing()
    {
        _h.Engine.Answer = (_, _) => Task.FromResult("""{"ok":true,"handled":true}""");

        _h.Page(PageMessages.Download("https://c411.org/dl/1", "film.torrent"));

        var asked = JsonNode.Parse(Assert.Single(_h.Engine.Sent))!;
        Assert.Equal("__download", (string?)asked["type"]);
        Assert.Equal("https://c411.org/dl/1", (string?)asked["url"]);
        Assert.Equal("film.torrent", (string?)asked["filename"]);
        await Task.Delay(100);
        Assert.Empty(_h.Shell.Toasts);
    }

    [Theory]
    [InlineData("""{"ok":true}""")]
    [InlineData("""{"ok":true,"handled":false}""")]
    [InlineData("""{"ok":true,"handled":"true"}""")]
    [InlineData("not json")]
    [InlineData("[true]")]
    public async Task A_download_the_engine_does_not_take_is_reported_to_the_user(string answer)
    {
        _h.Engine.Answer = (_, _) => Task.FromResult(answer);

        _h.Page(PageMessages.Download("https://c411.org/dl/1", "film.torrent"));

        await _h.WaitForToastAsync("Délai dépassé : réessaie.");
    }

    [Fact]
    public async Task A_download_nobody_answers_is_reported_after_the_delay()
    {
        using var h = new PageHarness(engineTimeout: TimeSpan.FromMilliseconds(100));
        h.Engine.Answer = async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return "";
        };

        h.Page(PageMessages.Download("https://c411.org/dl/1", "film.torrent"));

        await h.WaitForToastAsync("Délai dépassé : réessaie.");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void A_download_needs_an_address(string? url)
    {
        _h.Page(new JsonObject { ["t"] = "download", ["url"] = url, ["filename"] = "film.torrent" }.ToJsonString());

        Assert.Empty(_h.Engine.Sent);
        Assert.Empty(_h.Shell.Toasts);
    }

    [Fact]
    public void A_download_without_a_name_is_still_offered_to_the_engine()
    {
        _h.Page("""{"t":"download","url":"https://c411.org/dl/1"}""");

        var asked = JsonNode.Parse(Assert.Single(_h.Engine.Sent))!;
        Assert.Equal("", (string?)asked["filename"]);
    }

    [Fact]
    public void A_download_with_an_absurd_address_or_name_is_cut_down()
    {
        _h.Page(PageMessages.Download("https://c411.org/" + new string('a', 9000), "x.torrent"));
        Assert.Empty(_h.Engine.Sent);

        _h.Page(PageMessages.Download("https://c411.org/dl/1", new string('n', 600)));
        var asked = JsonNode.Parse(Assert.Single(_h.Engine.Sent))!;
        Assert.Equal(260, ((string)asked["filename"]!).Length);
    }

    [Fact]
    public async Task A_browser_download_the_engine_takes_over_is_refused()
    {
        _h.Engine.Answer = (_, _) => Task.FromResult("""{"ok":true,"handled":true}""");

        Assert.Null(await _h.Bridge.PlanDownloadAsync("https://c411.org/dl/1", "film.torrent"));

        Assert.Empty(_h.Files);
        Assert.Empty(_h.Shell.Toasts);
    }

    [Fact]
    public async Task A_browser_download_is_saved_under_a_safe_name_in_the_downloads_folder()
    {
        var path = await _h.Bridge.PlanDownloadAsync("https://c411.org/dl/1", @"..\..\Windows\film.torrent");

        Assert.NotNull(path);
        Assert.Equal(_h.Downloads, Path.GetDirectoryName(path));
        Assert.Equal("film.torrent", Path.GetFileName(path));
        Assert.True(File.Exists(path));
        Expect.Equal(["film.torrent"], _h.Files);

        // The engine was asked about the same, safe name.
        var asked = JsonNode.Parse(Assert.Single(_h.Engine.Sent))!;
        Assert.Equal("film.torrent", (string?)asked["filename"]);
    }

    [Fact]
    public async Task Browser_downloads_of_the_same_name_do_not_overwrite_each_other()
    {
        var first = await _h.Bridge.PlanDownloadAsync("https://c411.org/dl/1", "film.torrent");
        var second = await _h.Bridge.PlanDownloadAsync("https://c411.org/dl/2", "film.torrent");

        Assert.NotEqual(first, second);
        Expect.Equal(["film (1).torrent", "film.torrent"], _h.Files);
    }

    [Fact]
    public async Task Downloading_in_a_loop_is_stopped()
    {
        for (var i = 0; i < PageBridge.MaxDownloadsPerWindow; i++)
        {
            Assert.NotNull(await _h.Bridge.PlanDownloadAsync("https://c411.org/dl/" + i, $"film{i}.torrent"));
        }

        Assert.Null(await _h.Bridge.PlanDownloadAsync("https://c411.org/dl/more", "more.torrent"));

        Assert.Equal(PageBridge.MaxDownloadsPerWindow, _h.Files.Length);
        Expect.Equal(["Trop de téléchargements d'un coup : réessaie."], _h.Shell.Toasts);
    }

    [Fact]
    public async Task Downloads_that_are_refused_or_taken_over_do_not_use_the_allowance_of_downloads()
    {
        for (var i = 0; i < PageBridge.MaxDownloadsPerWindow; i++) Assert.Null(await _h.Bridge.PlanDownloadAsync("https://c411.org/dl/" + i, $"setup{i}.exe"));

        _h.Engine.Answer = (_, _) => Task.FromResult("""{"ok":true,"handled":true}""");
        for (var i = 0; i < PageBridge.MaxDownloadsPerWindow; i++) Assert.Null(await _h.Bridge.PlanDownloadAsync("https://c411.org/dl/" + i, $"film{i}.torrent"));

        _h.Engine.Answer = (_, _) => Task.FromResult("""{"ok":true}""");
        Assert.NotNull(await _h.Bridge.PlanDownloadAsync("https://c411.org/dl/ok", "film.torrent"));
    }

    [Theory]
    [InlineData("setup.exe")]
    [InlineData("page.html")]
    [InlineData("link.lnk")]
    [InlineData("script.js")]
    [InlineData("film.torrent.exe")]
    [InlineData("noextension")]
    [InlineData("")]
    public async Task A_browser_download_of_another_kind_is_refused(string name)
    {
        Assert.Null(await _h.Bridge.PlanDownloadAsync("https://c411.org/dl/1", name));

        Assert.Empty(_h.Files);
        Assert.False(Directory.Exists(_h.Downloads));
        Assert.StartsWith("Type de fichier refusé : ", Assert.Single(_h.Shell.Toasts));
    }

    [Theory]
    [InlineData("""{"ok":true}""")]
    [InlineData("not json")]
    public async Task A_browser_download_goes_on_when_the_engine_does_not_take_it(string answer)
    {
        _h.Engine.Answer = (_, _) => Task.FromResult(answer);

        Assert.NotNull(await _h.Bridge.PlanDownloadAsync("https://c411.org/dl/1", "film.torrent"));
    }

    [Fact]
    public async Task A_browser_download_goes_on_when_the_engine_is_too_slow()
    {
        using var h = new PageHarness(engineTimeout: TimeSpan.FromMilliseconds(100));
        h.Engine.Answer = async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return "";
        };

        Assert.NotNull(await h.Bridge.PlanDownloadAsync("https://c411.org/dl/1", "film.torrent"));
    }

    [Fact]
    public async Task A_browser_download_is_refused_when_the_downloads_folder_cannot_be_used()
    {
        await File.WriteAllTextAsync(_h.Downloads, "a file where the folder should be");

        Assert.Null(await _h.Bridge.PlanDownloadAsync("https://c411.org/dl/1", "film.torrent"));

        Expect.Equal(["Téléchargement impossible : dossier inaccessible"], _h.Shell.Toasts);
    }

    [Fact]
    public async Task A_finished_browser_download_is_announced()
    {
        var path = (await _h.Bridge.PlanDownloadAsync("https://c411.org/dl/1", "film.torrent"))!;
        await File.WriteAllBytesAsync(path, [1, 2, 3]);

        _h.Bridge.DownloadFinished(path, success: true);

        Expect.Equal(["Enregistré dans Téléchargements : film.torrent"], _h.Shell.Toasts);
        Expect.Equal(["film.torrent"], _h.Files);
    }

    [Fact]
    public async Task A_failed_browser_download_leaves_nothing_empty_behind()
    {
        var path = (await _h.Bridge.PlanDownloadAsync("https://c411.org/dl/1", "film.torrent"))!;

        _h.Bridge.DownloadFinished(path, success: false);

        Expect.Equal(["Téléchargement impossible : film.torrent"], _h.Shell.Toasts);
        Assert.Empty(_h.Files);
    }

    [Fact]
    public async Task A_failed_browser_download_keeps_what_was_received()
    {
        var path = (await _h.Bridge.PlanDownloadAsync("https://c411.org/dl/1", "film.torrent"))!;
        await File.WriteAllBytesAsync(path, [1, 2, 3]);

        _h.Bridge.DownloadFinished(path, success: false);

        Expect.Equal(["film.torrent"], _h.Files);
    }

    // The browser saves what the page downloads (and marks it as coming from the Internet): the page has no way to write a file itself.
    [Theory]
    [InlineData("film.torrent")]
    [InlineData("setup.exe")]
    [InlineData("film.torrent:Zone.Identifier")]
    [InlineData(@"..\..\evil.torrent")]
    [InlineData("")]
    public void A_page_cannot_write_a_file_of_its_own(string name)
    {
        _h.Page(PageMessages.SaveFile(name, [1, 2, 3]));
        _h.Page("""{"t":"saveFile","name":"film.torrent"}""");
        _h.Page("""{"t":"saveFile","base64":"AQ=="}""");

        Assert.Empty(_h.Files);
        Assert.False(Directory.Exists(_h.Downloads));
        Assert.Empty(_h.Shell.Toasts);
        Assert.Empty(_h.Posted);
        Assert.Empty(_h.Engine.Sent);
    }

    [Fact]
    public async Task A_page_that_nobody_sees_is_still_served_for_a_while()
    {
        _h.Bridge.SetOnScreen(false);

        _h.Page(PageMessages.Send("a1", Status()));

        var reply = await _h.WaitForAsync(PageMessages.Reply("a1"));
        Assert.Equal("""{"ok":true}""", (string?)reply["json"]);
        Assert.Single(_h.Engine.Sent);
    }

    [Fact]
    public async Task A_page_that_nobody_sees_is_refused_once_the_grace_is_over()
    {
        using var h = new PageHarness(offScreenGrace: TimeSpan.Zero);
        h.Bridge.SetOnScreen(false);

        h.Page(PageMessages.Send("a1", new JsonObject { ["type"] = "upload", ["base64"] = "AAAA", ["filename"] = "film.torrent" }));

        var reply = await h.WaitForAsync(PageMessages.Reply("a1"));
        Assert.Equal("""{"ok":false,"error":"Onglet C411 masqué : reviens sur la page et réessaie."}""", (string?)reply["json"]);
        Assert.Empty(h.Engine.Sent);
    }

    [Fact]
    public async Task A_page_is_served_again_when_it_comes_back_on_screen()
    {
        using var h = new PageHarness(offScreenGrace: TimeSpan.Zero);
        h.Bridge.SetOnScreen(false);
        h.Page(PageMessages.Send("hidden", Status()));
        await h.WaitForAsync(PageMessages.Reply("hidden"));
        Assert.Empty(h.Engine.Sent);

        h.Bridge.SetOnScreen(true);
        h.Page(PageMessages.Send("back", Status()));

        var reply = await h.WaitForAsync(PageMessages.Reply("back"));
        Assert.Equal("""{"ok":true}""", (string?)reply["json"]);
        Assert.Single(h.Engine.Sent);
    }

    [Fact]
    public async Task Saying_again_that_the_page_is_out_of_sight_does_not_start_the_grace_over()
    {
        using var h = new PageHarness(offScreenGrace: TimeSpan.FromMilliseconds(150));
        h.Bridge.SetOnScreen(false);
        await Task.Delay(400);

        h.Bridge.SetOnScreen(false);
        h.Page(PageMessages.Send("a1", Status()));

        var reply = await h.WaitForAsync(PageMessages.Reply("a1"));
        Assert.Contains("masqué", (string?)reply["json"], StringComparison.Ordinal);
        Assert.Empty(h.Engine.Sent);
    }

    [Fact]
    public async Task A_page_that_is_never_told_it_is_out_of_sight_is_served()
    {
        using var h = new PageHarness(offScreenGrace: TimeSpan.Zero);
        h.Bridge.SetOnScreen(true);

        h.Page(PageMessages.Send("a1", Status()));

        await h.WaitForAsync(PageMessages.Reply("a1"));
        Assert.Single(h.Engine.Sent);
    }

    [Fact]
    public async Task Asking_in_a_loop_is_stopped()
    {
        for (var i = 0; i < PageBridge.MaxRequestsPerWindow + 3; i++) _h.Page(PageMessages.Send("a" + i, Status()));

        for (var i = PageBridge.MaxRequestsPerWindow; i < PageBridge.MaxRequestsPerWindow + 3; i++)
        {
            var reply = await _h.WaitForAsync(PageMessages.Reply("a" + i));
            Assert.Equal("""{"ok":false,"error":"Trop de requêtes d'un coup : réessaie."}""", (string?)reply["json"]);
        }

        Assert.Equal(PageBridge.MaxRequestsPerWindow, _h.Engine.Sent.Length);
    }

    [Fact]
    public async Task Refused_messages_count_as_requests_too()
    {
        for (var i = 0; i < PageBridge.MaxRequestsPerWindow; i++) _h.Page(PageMessages.Send("bad" + i, new JsonObject { ["type"] = "saveConfig" }));

        _h.Page(PageMessages.Send("good", Status()));

        var reply = await _h.WaitForAsync(PageMessages.Reply("good"));
        Assert.Contains("Trop de requêtes", (string?)reply["json"], StringComparison.Ordinal);
        Assert.Empty(_h.Engine.Sent);
    }

    [Fact]
    public async Task A_page_that_has_waited_for_the_window_to_pass_is_served_again()
    {
        using var h = new PageHarness(rateWindow: TimeSpan.FromMilliseconds(500));
        for (var i = 0; i <= PageBridge.MaxRequestsPerWindow; i++) h.Page(PageMessages.Send("a" + i, Status()));
        var refused = await h.WaitForAsync(PageMessages.Reply("a" + PageBridge.MaxRequestsPerWindow));
        Assert.Contains("Trop de requêtes", (string?)refused["json"], StringComparison.Ordinal);

        await Task.Delay(700);
        h.Page(PageMessages.Send("again", Status()));

        var reply = await h.WaitForAsync(PageMessages.Reply("again"));
        Assert.Equal("""{"ok":true}""", (string?)reply["json"]);
        Assert.Equal(PageBridge.MaxRequestsPerWindow + 1, h.Engine.Sent.Length);
    }

    [Fact]
    public async Task Sending_torrents_in_a_loop_is_stopped_while_the_other_messages_go_on()
    {
        for (var i = 0; i < PageBridge.MaxUploadsPerWindow + 2; i++)
        {
            _h.Page(PageMessages.Send("up" + i, new JsonObject { ["type"] = "upload", ["base64"] = "AAAA", ["filename"] = $"film{i}.torrent" }));
        }

        for (var i = PageBridge.MaxUploadsPerWindow; i < PageBridge.MaxUploadsPerWindow + 2; i++)
        {
            var reply = await _h.WaitForAsync(PageMessages.Reply("up" + i));
            Assert.Equal("""{"ok":false,"error":"Trop d'envois d'un coup : réessaie."}""", (string?)reply["json"]);
        }

        Assert.Equal(PageBridge.MaxUploadsPerWindow, _h.Engine.Sent.Length);

        _h.Page(PageMessages.Send("categories", new JsonObject { ["type"] = "getCategories" }));
        var other = await _h.WaitForAsync(PageMessages.Reply("categories"));
        Assert.Equal("""{"ok":true}""", (string?)other["json"]);
        Assert.Equal(PageBridge.MaxUploadsPerWindow + 1, _h.Engine.Sent.Length);
    }

    [Fact]
    public async Task Messages_other_than_uploads_do_not_use_the_allowance_of_uploads()
    {
        for (var i = 0; i < PageBridge.MaxUploadsPerWindow; i++) _h.Page(PageMessages.Send("s" + i, Status()));

        _h.Page(PageMessages.Send("up", new JsonObject { ["type"] = "upload", ["base64"] = "AAAA", ["filename"] = "film.torrent" }));

        var reply = await _h.WaitForAsync(PageMessages.Reply("up"));
        Assert.Equal("""{"ok":true}""", (string?)reply["json"]);
    }

    [Fact]
    public void What_is_dropped_on_the_page_reaches_the_window_checked()
    {
        _h.Page(PageMessages.Drop("magnet:?xt=urn:btih:abc", ("film.torrent", Convert.ToBase64String("d4:spam4:eggse"u8)), ("photo.png", "ZA==")));

        var drop = Assert.Single(_h.Shell.Drops);
        Assert.Equal(["magnet:?xt=urn:btih:abc"], drop.Request.Magnets);
        Assert.Equal(["film.torrent"], drop.Files.Select(f => f.Name).ToArray());
        Assert.Equal(["« photo.png » n’est pas un fichier .torrent"], drop.Problems);
        Assert.Empty(_h.Shell.Toasts);
    }

    [Fact]
    public void Dropping_in_a_loop_is_stopped()
    {
        for (var i = 0; i < PageBridge.MaxDropsPerWindow + 3; i++) _h.Page(PageMessages.Drop("magnet:?xt=urn:btih:abc"));

        Assert.Equal(PageBridge.MaxDropsPerWindow, _h.Shell.Drops.Length);
        Assert.Equal(3, _h.Shell.Toasts.Count(t => t.StartsWith("Trop de dépôts", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Drops_and_requests_are_limited_separately()
    {
        for (var i = 0; i < PageBridge.MaxDropsPerWindow + 1; i++) _h.Page(PageMessages.Drop("magnet:?xt=urn:btih:abc"));

        _h.Page(PageMessages.Send("a1", Status()));

        var reply = await _h.WaitForAsync(PageMessages.Reply("a1"));
        Assert.Equal("""{"ok":true}""", (string?)reply["json"]);
        Assert.Equal(PageBridge.MaxDropsPerWindow, _h.Shell.Drops.Length);
    }

    [Fact]
    public void A_drop_cannot_do_what_a_command_line_does()
    {
        _h.Page(PageMessages.Drop(@"--quit --tab=qbit \\127.0.0.1\share\film.torrent"));

        var drop = Assert.Single(_h.Shell.Drops);
        Assert.False(drop.HasWork);
        Assert.False(drop.Request.Quit);
        Assert.Empty(drop.Request.TorrentFiles);
    }

    [Fact]
    public async Task A_file_is_fetched_with_the_cookies_and_the_agent_of_the_tab()
    {
        using var h = new PageHarness(network: (_, _) => Task.FromResult(StubHandler.Reply(HttpStatusCode.OK, "hello"u8.ToArray())));
        h.Shell.UserAgent = "Tab/9";
        h.Shell.Cookies = _ => Task.FromResult<string?>("sid=1");

        h.Page(PageMessages.FetchBytes("f1", "https://c411.org/dl/1", referer: "https://c411.org/torrents/abc"));

        var reply = await h.WaitForAsync(PageMessages.Reply("f1"));
        var body = JsonNode.Parse((string)reply["json"]!)!;
        Assert.Equal(200, (int)body["status"]!);
        Assert.Equal("hello", Encoding.UTF8.GetString(Convert.FromBase64String((string)body["base64"]!)));
        var request = Assert.Single(h.Network.Requests);
        Assert.Equal("https://c411.org/dl/1", request.Target);
        Assert.Equal("Tab/9", request.Header("User-Agent"));
        Assert.Equal("sid=1", request.Header("Cookie"));
        Assert.Equal("https://c411.org/torrents/abc", request.Header("Referer"));
    }

    [Fact]
    public async Task A_status_other_than_success_is_given_to_the_page_as_it_is()
    {
        using var h = new PageHarness(network: (_, _) => Task.FromResult(StubHandler.Reply(HttpStatusCode.NotFound, "nope"u8.ToArray())));

        h.Page(PageMessages.FetchBytes("f1", "https://c411.org/dl/1"));

        var body = JsonNode.Parse((string)(await h.WaitForAsync(PageMessages.Reply("f1")))["json"]!)!;
        Assert.Equal(404, (int)body["status"]!);
        Assert.Equal("nope", Encoding.UTF8.GetString(Convert.FromBase64String((string)body["base64"]!)));
    }

    [Theory]
    [InlineData("http://c411.org/dl/1")]
    [InlineData("https://evil.example/dl/1")]
    [InlineData("https://127.0.0.1/dl/1")]
    [InlineData("file:///C:/Windows/win.ini")]
    [InlineData("")]
    public async Task A_file_that_may_not_be_fetched_is_reported_and_never_requested(string url)
    {
        _h.Page(PageMessages.FetchBytes("f1", url));

        var reply = await _h.WaitForAsync(PageMessages.Reply("f1"));
        Assert.Equal("""{"error":"URL non supportée"}""", (string?)reply["json"]);
        Assert.Empty(_h.Network.Requests);
    }

    [Fact]
    public void A_fetch_without_an_id_is_ignored()
    {
        _h.Page("""{"t":"fetchBytes","url":"https://c411.org/dl/1"}""");

        Assert.Empty(_h.Network.Requests);
        Assert.Empty(_h.Posted);
    }

    [Fact]
    public async Task Only_a_few_fetches_run_at_once_and_the_others_wait_for_a_free_place()
    {
        var gate = new TaskCompletionSource();
        using var h = new PageHarness(network: async (_, token) =>
        {
            await gate.Task.WaitAsync(token);
            return StubHandler.Reply(HttpStatusCode.OK, "x"u8.ToArray());
        });
        for (var i = 0; i < PageBridge.MaxParallelFetches; i++) h.Page(PageMessages.FetchBytes("f" + i, "https://c411.org/dl/" + i));

        h.Page(PageMessages.FetchBytes("extra", "https://c411.org/dl/x"));
        var refused = await h.WaitForAsync(PageMessages.Reply("extra"));
        Assert.Equal(TooManyFetches, (string?)refused["json"]);

        gate.SetResult();
        for (var i = 0; i < PageBridge.MaxParallelFetches; i++) await h.WaitForAsync(PageMessages.Reply("f" + i));

        // Places are given back (the last one a moment after the last answer).
        for (var attempt = 0; ; attempt++)
        {
            var id = "again" + attempt;
            h.Page(PageMessages.FetchBytes(id, "https://c411.org/dl/y"));
            var answer = (string)(await h.WaitForAsync(PageMessages.Reply(id)))["json"]!;
            if (answer != TooManyFetches) break;

            Assert.True(attempt < 100, "The places of the finished fetches were never given back.");
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task The_answer_of_a_fetch_for_a_page_that_is_gone_is_dropped()
    {
        var gate = new TaskCompletionSource();
        using var h = new PageHarness(network: async (_, token) =>
        {
            await gate.Task.WaitAsync(token);
            return StubHandler.Reply(HttpStatusCode.OK, "x"u8.ToArray());
        });
        h.Page(PageMessages.FetchBytes("f1", "https://c411.org/dl/1"));

        h.Bridge.NewPage();
        gate.SetResult();

        // Nothing is posted: the answer was for the document that is gone.
        await Task.Delay(300);
        Assert.Empty(h.Posted);
    }
}

public sealed class PageBridgeEngineTests : IDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    private readonly EngineHarness _engine = new();
    private readonly TempDir _dir = new();
    private readonly FakePageShell _shell = new();
    private readonly StubHandler _network = new(_ => StubHandler.Reply(HttpStatusCode.OK));
    private readonly PageFetcher _fetcher;
    private readonly PageBridge _page;
    private readonly List<JsonObject> _posted = [];

    public PageBridgeEngineTests()
    {
        _fetcher = new PageFetcher(_network);
        _page = new PageBridge(_engine.Bridge, _shell, _fetcher, _dir.File("Downloads"), Collect);
        _engine.Bridge.BuildState();
        _engine.Bridge.OnMessage("""{"t":"ready"}""");
    }

    public void Dispose()
    {
        _page.Dispose();
        _fetcher.Dispose();
        _network.Dispose();
        _engine.Dispose();
        _dir.Dispose();
    }

    private void Collect(string json)
    {
        var message = JsonNode.Parse(json)!.AsObject();
        lock (_posted) _posted.Add(message);
    }

    private async Task<JsonObject> WaitForReplyAsync(string id)
    {
        var deadline = Environment.TickCount64 + (long)Patience.TotalMilliseconds;
        while (Environment.TickCount64 < deadline)
        {
            lock (_posted)
            {
                var found = _posted.FirstOrDefault(PageMessages.Reply(id));
                if (found is not null) return found;
            }

            await Task.Delay(10);
        }

        Assert.Fail("The page got no answer.");
        return null!;
    }

    private Task<JsonObject> NextHandleAsync() => _engine.WaitForAsync(p => (string?)p["t"] == "handle");

    private void EngineReplies(JsonObject handle, string json) =>
        _engine.Bridge.OnMessage(new JsonObject { ["t"] = "reply", ["id"] = (string?)handle["id"], ["json"] = json }.ToJsonString());

    [Fact]
    public async Task A_message_of_the_page_is_answered_by_the_engine()
    {
        _page.OnMessage(PageMessages.Send("p1", new JsonObject { ["type"] = "pageStatus" }));

        var handle = await NextHandleAsync();
        Assert.Equal("""{"type":"pageStatus"}""", (string?)handle["raw"]);
        EngineReplies(handle, """{"ok":true,"qbit":"up"}""");

        var reply = await WaitForReplyAsync("p1");
        Assert.Equal("""{"ok":true,"qbit":"up"}""", (string?)reply["json"]);
    }

    [Fact]
    public async Task A_message_the_page_may_not_send_does_not_reach_the_engine()
    {
        _page.OnMessage(PageMessages.Send("p1", new JsonObject { ["type"] = "saveConfig" }));

        var reply = await WaitForReplyAsync("p1");
        Assert.Contains("Message refusé", (string?)reply["json"]);
        Assert.Empty(_engine.Handles);
    }

    [Fact]
    public async Task A_download_is_taken_over_by_the_engine_when_it_says_so()
    {
        var plan = _page.PlanDownloadAsync("https://c411.org/dl/1", "film.torrent");

        var handle = await NextHandleAsync();
        var asked = JsonNode.Parse((string)handle["raw"]!)!;
        Assert.Equal("__download", (string?)asked["type"]);
        Assert.Equal("https://c411.org/dl/1", (string?)asked["url"]);
        EngineReplies(handle, """{"ok":true,"handled":true}""");

        Assert.Null(await plan.WaitAsync(Patience));
        Assert.False(Directory.Exists(_dir.File("Downloads")));
    }

    [Fact]
    public async Task A_download_is_saved_by_the_browser_when_the_engine_does_not_take_it()
    {
        var plan = _page.PlanDownloadAsync("https://c411.org/dl/1", "film.torrent");

        EngineReplies(await NextHandleAsync(), """{"ok":true,"handled":false}""");

        var path = await plan.WaitAsync(Patience);
        Assert.Equal(_dir.File(Path.Combine("Downloads", "film.torrent")), path);
    }

    [Fact]
    public async Task A_page_request_made_while_the_engine_restarts_gets_the_error_of_the_engine()
    {
        _page.OnMessage(PageMessages.Send("p1", new JsonObject { ["type"] = "pageStatus" }));
        await NextHandleAsync();

        _engine.Bridge.PageLost();

        var reply = await WaitForReplyAsync("p1");
        var answer = JsonNode.Parse((string)reply["json"]!)!;
        Assert.False((bool)answer["ok"]!);
        Assert.Contains("redémarré", (string?)answer["error"]);
    }
}
