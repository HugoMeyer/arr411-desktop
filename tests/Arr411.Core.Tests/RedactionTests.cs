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
