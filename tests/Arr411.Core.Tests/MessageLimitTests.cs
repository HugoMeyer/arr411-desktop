using System.Text.Json.Nodes;
using Arr411.Core.Bridge;
using Arr411.Core.Files;

namespace Arr411.Core.Tests;

/// <summary>
/// A message longer than the limit is not even parsed. The messages are valid JSON followed by blanks, so that only their length
/// tells the one that is read from the one that is not.
/// </summary>
public sealed class MessageLimitTests
{
    private static string Padded(string message, int length) =>
        string.Create(length, message, (span, text) =>
        {
            text.AsSpan().CopyTo(span);
            span[text.Length..].Fill(' ');
        });

    [Fact]
    public void The_web_app_is_read_up_to_the_limit_and_not_beyond()
    {
        using var h = new BridgeHarness();
        const string toast = """{"t":"toast","text":"hello"}""";

        h.Bridge.OnMessage(Padded(toast, MessageLimit.MaxChars + 1));
        Assert.Empty(h.Window.Calls);

        h.Bridge.OnMessage(Padded(toast, MessageLimit.MaxChars));
        Assert.Equal(["toast:hello"], h.Window.Calls);
    }

    [Fact]
    public void The_engine_is_read_up_to_the_limit_and_not_beyond()
    {
        using var h = new EngineHarness();
        const string notify = """{"t":"notify","title":"a","text":"b"}""";

        h.Bridge.OnMessage(Padded(notify, MessageLimit.MaxChars + 1));
        Assert.Empty(h.Shell.Calls);

        h.Bridge.OnMessage(Padded(notify, MessageLimit.MaxChars));
        Assert.Equal(["notify:a|b"], h.Shell.Calls);
    }

    [Fact]
    public void The_c411_page_is_read_up_to_the_limit_and_not_beyond()
    {
        using var h = new PageHarness();
        const string toast = """{"t":"toast","text":"hello"}""";

        h.Page(Padded(toast, PageBridge.MaxMessageChars + 1));
        Assert.Empty(h.Shell.Toasts);

        h.Page(Padded(toast, PageBridge.MaxMessageChars));
        Assert.Equal(["hello"], h.Shell.Toasts);
    }

    // What the page says is short. A message that is not is answered (its id is at the start) without being parsed: were it parsed, it would not be JSON.
    [Fact]
    public async Task A_long_message_that_is_not_an_upload_is_refused_unread()
    {
        using var h = new PageHarness();

        h.Page("{\"t\":\"send\",\"id\":\"big\",\"msg\":\"" + new string('x', PageBridge.MaxMessageChars));

        var reply = await h.WaitForAsync(PageMessages.Reply("big"));
        Assert.Equal("""{"ok":false,"error":"Message trop volumineux"}""", (string?)reply["json"]);
        Assert.Empty(h.Engine.Sent);
    }

    [Fact]
    public async Task A_message_beyond_the_limit_of_uploads_is_refused_unread()
    {
        using var h = new PageHarness();
        const string start = "{\"t\":\"send\",\"id\":\"huge\",\"msg\":\"{\\\"type\\\":\\\"upload\\\"";

        h.Page(start + new string('x', PageBridge.MaxUploadMessageChars));

        var reply = await h.WaitForAsync(PageMessages.Reply("huge"));
        Assert.Equal("""{"ok":false,"error":"Message trop volumineux"}""", (string?)reply["json"]);
        Assert.Empty(h.Engine.Sent);
    }

    [Fact]
    public async Task The_biggest_torrent_the_c411_page_may_upload_is_under_the_limit_and_read()
    {
        using var h = new PageHarness();
        var base64 = Convert.ToBase64String(new byte[16 * 1024 * 1024]);
        var message = PageMessages.Send("up", new JsonObject { ["type"] = "upload", ["base64"] = base64, ["filename"] = "film.torrent" });
        Assert.InRange(message.Length, PageBridge.MaxMessageChars + 1, PageBridge.MaxUploadMessageChars);

        h.Page(message);

        var reply = await h.WaitForAsync(PageMessages.Reply("up"), timeoutMs: 30_000);
        Assert.Equal("""{"ok":true}""", (string?)reply["json"]);
        Assert.Equal(base64.Length, ((string)JsonNode.Parse(Assert.Single(h.Engine.Sent))!["base64"]!).Length);
    }

    // A long upload is the only message that is read in spite of its length: the user must be there, and it counts as an upload before it is read.
    [Fact]
    public async Task A_long_upload_needs_the_user_and_is_counted_before_it_is_read()
    {
        using var h = new PageHarness();
        static string Long(string id) => Padded(
            PageMessages.Send(id, new JsonObject { ["type"] = "upload", ["base64"] = "AAAA", ["filename"] = "film.torrent" }),
            PageBridge.MaxMessageChars + 1);

        h.Presence.Gesture = false;
        h.Page(Long("nobody"));

        var refused = await h.WaitForAsync(PageMessages.Reply("nobody"));
        Assert.Equal("""{"ok":false,"error":"Action refusée : l'onglet C411 n'est pas actif"}""", (string?)refused["json"]);
        Assert.Empty(h.Engine.Sent);

        h.Presence.Gesture = true;
        for (var i = 0; i <= PageBridge.MaxUploadsPerWindow; i++) h.Page(Long("up" + i));

        var last = await h.WaitForAsync(PageMessages.Reply("up" + PageBridge.MaxUploadsPerWindow));
        Assert.Equal("""{"ok":false,"error":"Trop d'envois d'un coup : réessaie."}""", (string?)last["json"]);
        Assert.Equal(PageBridge.MaxUploadsPerWindow, h.Engine.Sent.Length);
    }

    // The engine does the work for a long upload: nothing else the page says is allowed to be that long, whatever it pretends to be.
    [Fact]
    public async Task A_long_message_that_pretends_to_be_an_upload_is_not_served()
    {
        using var h = new PageHarness();
        var toast = new JsonObject { ["t"] = "toast", ["id"] = "upload", ["text"] = "hello" }.ToJsonString();
        var arm = PageMessages.Send("arm", new JsonObject { ["type"] = "arm", ["filename"] = "upload.torrent" });

        h.Page(Padded(toast, PageBridge.MaxMessageChars + 1));
        h.Page(Padded(arm, PageBridge.MaxMessageChars + 1));

        var reply = await h.WaitForAsync(PageMessages.Reply("arm"));
        Assert.Equal("""{"ok":false,"error":"Message refusé"}""", (string?)reply["json"]);
        Assert.Empty(h.Shell.Toasts);
        Assert.Empty(h.Engine.Sent);
    }

    [Fact]
    public void The_heaviest_legitimate_drop_is_under_the_limit_and_read()
    {
        using var h = new BridgeHarness();
        var torrent = new byte[TorrentFile.MaxBytes];
        torrent[0] = (byte)'d';
        var file = "{\"name\":\"film.torrent\",\"base64\":\"" + Convert.ToBase64String(torrent) + "\"}";

        // The most a drop may carry: files that fill the budget of the window, once in base64.
        var message = "{\"t\":\"drop\",\"files\":[" + file + "," + file + "]}";
        Assert.True(message.Length <= MessageLimit.MaxChars);
        h.Bridge.OnMessage(message);

        var drop = Assert.Single(h.Window.Drops);
        Assert.Equal(2, drop.Files.Count);
        Assert.Empty(drop.Problems);
    }
}
