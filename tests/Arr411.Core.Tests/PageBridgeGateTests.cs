using System.Text.Json.Nodes;
using Arr411.Core.Bridge;

namespace Arr411.Core.Tests;

/// <summary>
/// What the C411 page may do depends on whether the user is there (see IUserPresence): what only reads is served to a page that is on screen
/// (or left it a minute ago), what acts needs the tab on screen and a recent gesture.
/// </summary>
public sealed class PageBridgeGateTests : IDisposable
{
    private const string NotActive = """{"ok":false,"error":"Action refusée : l'onglet C411 n'est pas actif"}""";
    private const string Refused = """{"ok":false,"error":"Message refusé"}""";

    private readonly PageHarness _h = new();

    public void Dispose() => _h.Dispose();

    private static string Upload(string id) =>
        PageMessages.Send(id, new JsonObject { ["type"] = "upload", ["base64"] = "AAAA", ["filename"] = "film.torrent" });

    private static string Toast(string text) => new JsonObject { ["t"] = "toast", ["text"] = text }.ToJsonString();

    private async Task<string?> ReplyAsync(string id) => (string?)(await _h.WaitForAsync(PageMessages.Reply(id)))["json"];

    [Theory]
    [InlineData("arm")]
    [InlineData("upload")]
    public async Task A_message_that_acts_is_refused_without_a_recent_gesture_and_served_with_one(string type)
    {
        _h.Presence.Gesture = false;
        _h.Page(PageMessages.Send("a1", new JsonObject { ["type"] = type }));

        Assert.Equal(NotActive, await ReplyAsync("a1"));
        Assert.Empty(_h.Engine.Sent);

        _h.Presence.Gesture = true;
        _h.Page(PageMessages.Send("a2", new JsonObject { ["type"] = type }));

        Assert.Equal("""{"ok":true}""", await ReplyAsync("a2"));
        Assert.Single(_h.Engine.Sent);
        Assert.Equal(TimeSpan.FromSeconds(30), _h.Presence.GestureAskedFor);
    }

    [Theory]
    [InlineData("getCategories")]
    [InlineData("pageStatus")]
    [InlineData("qbitIndex")]
    [InlineData("arrMeta")]
    [InlineData("pagePrefs")]
    [InlineData("disarm")]
    public async Task A_message_that_only_reads_needs_no_gesture(string type)
    {
        _h.Presence.Gesture = false;

        _h.Page(PageMessages.Send("a1", new JsonObject { ["type"] = type }));

        Assert.Equal("""{"ok":true}""", await ReplyAsync("a1"));
        Assert.Single(_h.Engine.Sent);
    }

    [Fact]
    public void A_download_is_ignored_without_a_gesture_and_handed_to_the_engine_with_one()
    {
        _h.Presence.Gesture = false;
        _h.Page(PageMessages.Download("https://c411.org/dl/1", "film.torrent"));

        Assert.Empty(_h.Engine.Sent);
        Assert.Empty(_h.Shell.Toasts);

        _h.Presence.Gesture = true;
        _h.Page(PageMessages.Download("https://c411.org/dl/1", "film.torrent"));

        Assert.Single(_h.Engine.Sent);
    }

    [Fact]
    public async Task A_fetch_is_refused_without_a_gesture_and_made_with_one()
    {
        _h.Presence.Gesture = false;
        _h.Page(PageMessages.FetchBytes("f1", "https://c411.org/dl/1"));

        Assert.Equal(NotActive, await ReplyAsync("f1"));
        Assert.Empty(_h.Network.Requests);

        _h.Presence.Gesture = true;
        _h.Page(PageMessages.FetchBytes("f2", "https://c411.org/dl/1"));

        await _h.WaitForAsync(PageMessages.Reply("f2"));
        Assert.Single(_h.Network.Requests);
    }

    [Fact]
    public void A_notification_is_ignored_without_a_gesture_and_shown_with_one()
    {
        _h.Presence.Gesture = false;
        _h.Page(Toast("Enregistré dans Téléchargements : film.torrent"));

        Assert.Empty(_h.Shell.Toasts);

        _h.Presence.Gesture = true;
        _h.Page(Toast("Envoyé à qBittorrent"));

        Expect.Equal(["Envoyé à qBittorrent"], _h.Shell.Toasts);
    }

    // A file dropped from Explorer does not make our window the one in front: the pointer over the window is what tells the user's drop.
    [Fact]
    public void A_drop_needs_the_pointer_over_the_window_and_not_the_window_in_front()
    {
        _h.Presence.Gesture = false;
        _h.Presence.Drop = false;
        _h.Page(PageMessages.Drop("https://c411.org/"));

        Assert.Empty(_h.Shell.Drops);
        Assert.Empty(_h.Shell.Toasts);

        _h.Presence.Drop = true;
        _h.Page(PageMessages.Drop("https://c411.org/"));

        Assert.Single(_h.Shell.Drops);
        Assert.Equal(TimeSpan.FromSeconds(30), _h.Presence.DropAskedFor);
    }

    [Fact]
    public async Task A_tab_nobody_sees_cannot_act_even_when_the_user_is_there_but_is_still_read_for_a_while()
    {
        _h.Bridge.SetOnScreen(false);

        _h.Page(PageMessages.Drop("https://c411.org/"));
        _h.Page(Toast("Enregistré"));
        _h.Page(PageMessages.Download("https://c411.org/dl/1", "film.torrent"));
        _h.Page(PageMessages.FetchBytes("f1", "https://c411.org/dl/1"));
        _h.Page(Upload("up"));
        _h.Page(PageMessages.Send("arm", new JsonObject { ["type"] = "arm" }));
        _h.Page(PageMessages.Send("read", new JsonObject { ["type"] = "getCategories" }));

        Assert.Equal(NotActive, await ReplyAsync("f1"));
        Assert.Equal(NotActive, await ReplyAsync("up"));
        Assert.Equal(NotActive, await ReplyAsync("arm"));
        Assert.Equal("""{"ok":true}""", await ReplyAsync("read"));
        Assert.Empty(_h.Shell.Drops);
        Assert.Empty(_h.Shell.Toasts);
        Assert.Empty(_h.Network.Requests);
        Assert.Single(_h.Engine.Sent);
    }

    [Fact]
    public void Every_message_is_counted_before_it_is_read()
    {
        for (var i = 0; i < PageBridge.MaxRequestsPerWindow; i++) _h.Page("""{"t":"nobody"}""");

        _h.Page(Toast("hello"));
        _h.Page(PageMessages.Drop("https://c411.org/"));
        _h.Page(PageMessages.Download("https://c411.org/dl/1", "film.torrent"));

        Assert.Empty(_h.Shell.Toasts);
        Assert.Empty(_h.Shell.Drops);
        Assert.Empty(_h.Engine.Sent);
    }

    [Fact]
    public async Task Notifications_are_limited_to_one_in_a_while()
    {
        // 3 tenths of the window: 3 seconds with the real one.
        using var h = new PageHarness(rateWindow: TimeSpan.FromMilliseconds(400));
        h.Page(Toast("one"));
        h.Page(Toast("two"));

        Expect.Equal(["one"], h.Shell.Toasts);

        await Task.Delay(300);
        h.Page(Toast("three"));

        Expect.Equal(["one", "three"], h.Shell.Toasts);
    }

    [Theory]
    [InlineData("0123456789abcdef0123456789abcdef01234567")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF01234567")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    [InlineData("")]
    public async Task The_hash_of_a_page_status_is_a_hash_or_nothing(string hash)
    {
        _h.Page(PageMessages.Send("a1", new JsonObject { ["type"] = "pageStatus", ["hash"] = hash }));

        Assert.Equal("""{"ok":true}""", await ReplyAsync("a1"));
        Assert.Single(_h.Engine.Sent);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("abc&limit=1")]
    [InlineData("0123456789abcdef0123456789abcdef0123456&limit=1")]
    [InlineData("0123456789abcdef0123456789abcdef0123456")]
    [InlineData("0123456789abcdef0123456789abcdef012345678")]
    [InlineData("0123456789abcdef0123456789abcdef0123456g")]
    [InlineData("0123456789abcdef0123456789abcdef0123456/")]
    [InlineData(" 0123456789abcdef0123456789abcdef0123456")]
    public async Task Another_hash_of_a_page_status_is_refused(string hash)
    {
        _h.Page(PageMessages.Send("a1", new JsonObject { ["type"] = "pageStatus", ["hash"] = hash }));

        Assert.Equal(Refused, await ReplyAsync("a1"));
        Assert.Empty(_h.Engine.Sent);
    }

    [Fact]
    public async Task A_hash_of_a_page_status_that_is_not_text_is_refused()
    {
        _h.Page(PageMessages.Send("a1", new JsonObject { ["type"] = "pageStatus", ["hash"] = 42 }));
        _h.Page(PageMessages.Send("a2", new JsonObject { ["type"] = "pageStatus", ["hash"] = new JsonArray(PageMessages.Hash) }));

        Assert.Equal(Refused, await ReplyAsync("a1"));
        Assert.Equal(Refused, await ReplyAsync("a2"));
        Assert.Empty(_h.Engine.Sent);
    }

    [Fact]
    public async Task Every_hash_of_a_qbit_index_is_a_hash()
    {
        var upper = PageMessages.Hash.ToUpperInvariant();
        var v2 = PageMessages.Hash + PageMessages.Hash[..24];
        _h.Page(PageMessages.Send("ok", new JsonObject { ["type"] = "qbitIndex", ["hashes"] = new JsonArray(PageMessages.Hash, upper, v2) }));
        _h.Page(PageMessages.Send("none", new JsonObject { ["type"] = "qbitIndex", ["hashes"] = new JsonArray() }));
        _h.Page(PageMessages.Send("absent", new JsonObject { ["type"] = "qbitIndex" }));

        Assert.Equal("""{"ok":true}""", await ReplyAsync("ok"));
        Assert.Equal("""{"ok":true}""", await ReplyAsync("none"));
        Assert.Equal("""{"ok":true}""", await ReplyAsync("absent"));
        Assert.Equal(3, _h.Engine.Sent.Length);
    }

    [Theory]
    [InlineData("""["abc"]""")]
    [InlineData("""["0123456789abcdef0123456789abcdef01234567","&limit=1"]""")]
    [InlineData("""["0123456789abcdef0123456789abcdef01234567",null]""")]
    [InlineData("""["0123456789abcdef0123456789abcdef01234567",5]""")]
    [InlineData("""[["0123456789abcdef0123456789abcdef01234567"]]""")]
    [InlineData("\"0123456789abcdef0123456789abcdef01234567\"")]
    [InlineData("""{"0":"0123456789abcdef0123456789abcdef01234567"}""")]
    public async Task One_wrong_hash_or_a_list_that_is_not_one_refuses_the_whole_qbit_index(string hashes)
    {
        _h.Page(PageMessages.Send("a1", new JsonObject { ["type"] = "qbitIndex", ["hashes"] = JsonNode.Parse(hashes) }));

        Assert.Equal(Refused, await ReplyAsync("a1"));
        Assert.Empty(_h.Engine.Sent);
    }

    [Fact]
    public async Task A_qbit_index_asks_about_a_few_hundred_torrents_at_most()
    {
        static JsonArray Hashes(int count)
        {
            var hashes = new JsonArray();
            for (var i = 0; i < count; i++) hashes.Add(i.ToString("x40"));
            return hashes;
        }

        _h.Page(PageMessages.Send("max", new JsonObject { ["type"] = "qbitIndex", ["hashes"] = Hashes(300) }));
        _h.Page(PageMessages.Send("more", new JsonObject { ["type"] = "qbitIndex", ["hashes"] = Hashes(301) }));

        Assert.Equal("""{"ok":true}""", await ReplyAsync("max"));
        Assert.Equal(Refused, await ReplyAsync("more"));
        Assert.Single(_h.Engine.Sent);
    }
}

public sealed class UserInputTests
{
    [Theory]
    [InlineData(1_000u, 1_000u, 0)]
    [InlineData(1_000u, 31_000u, 30_000)]
    [InlineData(4_294_966_796u, 500u, 1_000)]
    public void The_age_of_the_last_input_follows_the_clock_around_its_wrap(uint last, uint now, int milliseconds)
    {
        Assert.Equal(TimeSpan.FromMilliseconds(milliseconds), UserInput.Age(last, now));
    }

    [Fact]
    public void An_input_that_seems_to_come_from_the_future_is_taken_for_a_very_old_one()
    {
        Assert.True(UserInput.Age(2_000u, 1_990u) > PageBridge.GestureWindow);
    }
}
