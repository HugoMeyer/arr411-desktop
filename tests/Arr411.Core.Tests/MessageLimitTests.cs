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

        h.Page(Padded(toast, MessageLimit.MaxChars + 1));
        Assert.Empty(h.Shell.Toasts);

        h.Page(Padded(toast, MessageLimit.MaxChars));
        Assert.Equal(["hello"], h.Shell.Toasts);
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
