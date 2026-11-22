using Arr411.Core.Diagnostics;

namespace Arr411.Core.Tests;

public sealed class RedactionTests
{
    private const string User = "hugos";
    private const string Profile = @"C:\Users\hugos";

    private static string Clean(string text) => Redaction.Clean(text, User, Profile);

    [Theory]
    [InlineData("login password=hunter2&next=1", "login password=***&next=1")]
    [InlineData("PASSWORD: hunter2 end", "PASSWORD: *** end")]
    [InlineData("""{"username":"bob","password":"s3 cr3t"}""", """{"username":"bob","password":***}""")]
    [InlineData("https://host/x?token=abc123&y=2", "https://host/x?token=***&y=2")]
    [InlineData("apikey=K-1;other=2", "apikey=***;other=2")]
    [InlineData("api_key: K-1", "api_key: ***")]
    [InlineData("Cookie: SID=abcdef; theme=dark", "Cookie: ***; theme=dark")]
    [InlineData("Authorization: Bearer eyJhbGciOi.payload.sig", "Authorization: ***")]
    [InlineData("sid=ZXCV", "sid=***")]
    public void What_opens_an_account_is_hidden(string text, string expected)
    {
        Assert.Equal(expected, Clean(text));
    }

    [Theory]
    [InlineData("http://admin:hunter2@192.168.1.5:8080/api", "http://***@192.168.1.5:8080/api")]
    [InlineData("ftp://user@host/file", "ftp://***@host/file")]
    [InlineData("https://user:p%40ss@example.org", "https://***@example.org")]
    public void The_credentials_inside_an_address_are_hidden(string text, string expected)
    {
        Assert.Equal(expected, Clean(text));
    }

    [Theory]
    [InlineData("https://c411.org/rss?cat=1&passkey=0123456789abcdef0123456789abcdef&n=2", "https://c411.org/rss?cat=1&passkey=***&n=2")]
    [InlineData("PassKey: 0123456789abcdef0123456789abcdef", "PassKey: ***")]
    [InlineData("""{"passkey":"0123456789abcdef0123456789abcdef","n":2}""", """{"passkey":***,"n":2}""")]
    [InlineData("https://c411.org/passkey/0123456789abcdef0123456789abcdef/x.torrent", "https://c411.org/passkey/***/x.torrent")]
    [InlineData("https://tracker.c411.org/announce/0123456789abcdef0123456789abcdef", "https://tracker.c411.org/announce/***")]
    [InlineData("https://tracker.c411.org/announce/0123456789ABCDEF0123456789ABCDEF?a=1", "https://tracker.c411.org/announce/***?a=1")]
    [InlineData("https://tracker.c411.org/0123456789abcdef0123456789abcdef/announce", "https://tracker.c411.org/***/announce")]
    [InlineData("announce: https://t.c411.org/announce/0123456789abcdef0123456789abcdef and https://t.c411.org/announce?passkey=ab12", "announce: https://t.c411.org/announce/*** and https://t.c411.org/announce?passkey=***")]
    public void The_passkey_of_the_tracker_is_hidden_wherever_it_is_in_an_address(string text, string expected)
    {
        Assert.Equal(expected, Clean(text));
    }

    [Theory]
    [InlineData("https://c411.org/torrents/0123456789abcdef0123456789abcdef01234567")]
    [InlineData("https://tracker.c411.org/announce")]
    [InlineData("https://tracker.c411.org/announce/")]
    [InlineData("https://tracker.c411.org/announce/status")]
    [InlineData("https://tracker.c411.org/0123456789abcdef0123456789abcdef01234567/announce")]
    public void An_address_without_a_passkey_is_left_as_it_is(string text)
    {
        Assert.Equal(text, Clean(text));
    }

    [Fact]
    public void The_profile_folder_and_the_account_name_are_hidden_whatever_the_case_or_the_slashes()
    {
        Assert.Equal(@"%USERPROFILE%\AppData\Local\Arr411\logs", Clean(@"C:\Users\hugos\AppData\Local\Arr411\logs"));
        Assert.Equal(@"%USERPROFILE%\AppData", Clean(@"c:\users\HUGOS\AppData"));
        Assert.Equal("%USERPROFILE%/Downloads", Clean("C:/Users/hugos/Downloads"));
        Assert.Equal("session of <user> on this PC", Clean("session of hugos on this PC"));
    }

    [Fact]
    public void A_short_account_name_is_not_cut_out_of_every_word()
    {
        Assert.Equal("a small example of me", Redaction.Clean("a small example of me", "me", @"C:\Users\me"));
    }

    [Theory]
    [InlineData("Notification shown: 3 torrents")]
    [InlineData("Arr411 Desktop 0.2.1 starting (process 1234)")]
    [InlineData("side=1 and inside=2 and decision=3")]
    [InlineData("The window has not answered for 12 s")]
    public void Ordinary_text_is_left_as_it_is(string text)
    {
        Assert.Equal(text, Clean(text));
    }
}
