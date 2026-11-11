using System.Text;
using System.Text.Json;

namespace Arr411.E2E;

/// <summary>What the web app does through the native bridge: network calls, covers, and the views built on them.</summary>
[Trait("Category", "E2E")]
public sealed class BridgeTests(ConfiguredApp fixture) : IClassFixture<ConfiguredApp>
{
    private AppProcess App => fixture.App;

    private CdpClient Page => fixture.App.Page;

    private FakeServices Fake => fixture.Fake;

    private object Get(string path) => new { method = "GET", url = Fake.BaseUrl + path, headers = new { } };

    private object Post(string path, string form) =>
        new { method = "POST", url = Fake.BaseUrl + path, headers = new { }, body = new { type = "text", text = form } };

    // The JSON the stand-in server answered with.
    private static JsonElement Echo(JsonElement answer)
    {
        Assert.True(answer.GetProperty("ok").GetBoolean(), answer.GetRawText());
        using var document = JsonDocument.Parse(answer.GetProperty("text").GetString()!);
        return document.RootElement.Clone();
    }

    private Task WaitForLibraryAsync() => Page.WaitForAsync("document.body.textContent.includes('Blade Runner 2049')", 30_000);

    [Theory]
    [InlineData("GET")]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public async Task Each_method_reaches_the_server(string method)
    {
        var answer = await Page.HttpAsync(new { method, url = Fake.BaseUrl + "/echo?a=1&b=%C3%A9", headers = new { } });

        var echo = Echo(answer);
        Assert.Equal(method, echo.GetProperty("method").GetString());
        Assert.Equal("?a=1&b=%C3%A9", echo.GetProperty("query").GetString());
    }

    [Fact]
    public async Task A_body_and_headers_arrive_untouched()
    {
        const string body = "{\"title\":\"Caf\u00e9 \U0001F600 \u2013 na\u00efve\"}";

        var answer = await Page.HttpAsync(new
        {
            method = "POST",
            url = Fake.BaseUrl + "/echo",
            headers = new Dictionary<string, string> { ["Content-Type"] = "application/json", ["X-Api-Key"] = "abc-123" },
            body = new { type = "text", text = body },
        });

        var echo = Echo(answer);
        Assert.Equal("application/json", echo.GetProperty("contentType").GetString());
        Assert.Equal("abc-123", echo.GetProperty("apiKey").GetString());
        Assert.Equal(body, echo.GetProperty("body").GetString());
    }

    [Fact]
    public async Task The_server_sees_a_native_client()
    {
        var echo = Echo(await Page.HttpAsync(Get("/echo")));

        Assert.Equal(Fake.BaseUrl, echo.GetProperty("origin").GetString());
        Assert.Equal(Fake.BaseUrl + "/", echo.GetProperty("referer").GetString());
        Assert.Equal("Arr411-Desktop/" + AppProcess.ExpectedVersion, echo.GetProperty("userAgent").GetString());
    }

    [Theory]
    [InlineData(401)]
    [InlineData(404)]
    [InlineData(500)]
    public async Task A_status_is_passed_on_with_its_body(int status)
    {
        var answer = await Page.HttpAsync(Get("/status/" + status));

        Assert.True(answer.GetProperty("ok").GetBoolean());
        Assert.Equal(status, answer.GetProperty("status").GetInt32());
        Assert.Equal("status " + status, answer.GetProperty("text").GetString());
    }

    [Fact]
    public async Task A_refused_connection_is_a_network_error()
    {
        var answer = await Page.HttpAsync(new { method = "GET", url = $"http://127.0.0.1:{Net.FreePort()}/", headers = new { } });

        Assert.False(answer.GetProperty("ok").GetBoolean());
        Assert.Equal("network", answer.GetProperty("code").GetString());
        Assert.StartsWith("R\u00e9seau", answer.GetProperty("message").GetString());
    }

    [Fact]
    public async Task An_invalid_address_is_reported()
    {
        var answer = await Page.HttpAsync(new { method = "GET", url = "not an address", headers = new { } });

        Assert.False(answer.GetProperty("ok").GetBoolean());
        Assert.Equal("network", answer.GetProperty("code").GetString());
        Assert.StartsWith("Adresse invalide", answer.GetProperty("message").GetString());
    }

    [Fact]
    public async Task A_big_answer_comes_through()
    {
        var length = await Page.EvaluateAsync<long>(
            $$"""App.http({ method: 'GET', url: {{Js.Quote(Fake.BaseUrl + "/big?mb=20")}}, headers: {} }).then(r => r.text.length)""",
            90_000);

        Assert.Equal(20L * 1024 * 1024, length);
    }

    [Fact]
    public async Task Cookies_are_kept_between_calls()
    {
        var name = "e2e" + Guid.NewGuid().ToString("N")[..8];

        Assert.DoesNotContain(name, Echo(await Page.HttpAsync(Get("/echo"))).GetProperty("cookie").GetString());

        var set = await Page.HttpAsync(Get($"/cookie/set?name={name}&value=1"));
        Assert.Equal(200, set.GetProperty("status").GetInt32());

        Assert.Contains(name + "=1", Echo(await Page.HttpAsync(Get("/echo"))).GetProperty("cookie").GetString());
    }

    [Fact]
    public async Task Logging_in_to_qBittorrent_works_through_the_bridge()
    {
        var wrong = await Page.HttpAsync(Post("/api/v2/auth/login", "username=admin&password=nope"));
        Assert.Equal("Fails.", wrong.GetProperty("text").GetString());

        var right = await Page.HttpAsync(Post("/api/v2/auth/login", $"username={FakeServices.QbUser}&password={FakeServices.QbPassword}"));
        Assert.Equal("Ok.", right.GetProperty("text").GetString());

        var version = await Page.HttpAsync(Get("/api/v2/app/webapiVersion"));
        Assert.Equal(200, version.GetProperty("status").GetInt32());
        Assert.Equal("2.11.4", version.GetProperty("text").GetString());
    }

    [Fact]
    public async Task A_multipart_form_goes_out_with_its_file()
    {
        const string torrent = "d8:announce12:caf\u00e9 tracker!e";
        const string magnet = "magnet:?xt=urn:btih:0123456789abcdef";

        var answer = await Page.HttpAsync(new
        {
            method = "POST",
            url = Fake.BaseUrl + "/upload",
            headers = new { },
            body = new
            {
                type = "multipart",
                parts = new object[]
                {
                    new { name = "urls", value = magnet },
                    new { name = "torrents", filename = "film.torrent", contentType = "application/x-bittorrent", base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(torrent)) },
                },
            },
        });

        var echo = Echo(answer);
        Assert.Equal(magnet, echo.GetProperty("fields").GetProperty("urls").GetString());
        var file = echo.GetProperty("files")[0];
        Assert.Equal("torrents", file.GetProperty("name").GetString());
        Assert.Equal("film.torrent", file.GetProperty("fileName").GetString());
        Assert.Equal("application/x-bittorrent", file.GetProperty("contentType").GetString());
        Assert.Equal(torrent, file.GetProperty("text").GetString());
        Assert.Equal(Encoding.UTF8.GetByteCount(torrent), file.GetProperty("length").GetInt32());
    }

    [Fact]
    public async Task The_app_opens_on_the_tab_of_the_settings()
    {
        Assert.Equal("radarr", await Page.EvaluateAsync<string>("App.state.tab"));
        Assert.Equal("radarr", await Page.EvaluateAsync<string>("Native.currentTab()"));
    }

    [Fact]
    public async Task The_radarr_library_is_listed()
    {
        await WaitForLibraryAsync();

        Assert.Contains(Fake.Requests, request => request.Path.StartsWith("/api/v3/movie", StringComparison.Ordinal) && request.ApiKey == FakeServices.ApiKey);
    }

    [Fact]
    public async Task Posters_come_through_the_cover_proxy()
    {
        await Page.WaitForAsync(
            """
            [...document.querySelectorAll('img')].some(img =>
              img.src.startsWith('https://arr411.local/mc?s=radarr&u=') && img.complete && img.naturalWidth > 0)
            """,
            30_000);

        Assert.Contains(Fake.Requests, request => request.Path.StartsWith("/api/v3/mediacover/1/", StringComparison.Ordinal) && request.ApiKey == FakeServices.ApiKey);

        // And the picture of the window shows it (the poster is the only thing with that colour).
        var pixels = 0;
        for (var attempt = 0; attempt < 10 && pixels <= 500; attempt++)
        {
            using var picture = await App.CaptureAsync("radarr-library");
            pixels = WindowCapture.CountNear(picture, FakeServices.PosterColor);
        }

        Assert.True(pixels > 500, $"The window shows {pixels} pixels of a poster.");
    }

    [Fact]
    public async Task The_radarr_view_loads_without_errors()
    {
        await WaitForLibraryAsync();
        await Task.Delay(1_000);

        Assert.Empty(Page.Problems());
    }

    [Fact]
    public async Task The_log_never_holds_secrets()
    {
        await Page.HttpAsync(Post("/api/v2/auth/login", $"username={FakeServices.QbUser}&password={FakeServices.QbPassword}"));
        await Page.HttpAsync(new { method = "GET", url = Fake.BaseUrl + "/echo", headers = new Dictionary<string, string> { ["X-Api-Key"] = FakeServices.ApiKey } });
        await Page.HttpAsync(new { method = "GET", url = $"http://127.0.0.1:{Net.FreePort()}/", headers = new { } });
        await WaitForLibraryAsync();

        var log = App.ReadLog();
        Assert.DoesNotContain(FakeServices.ApiKey, log);
        Assert.DoesNotContain(FakeServices.QbPassword, log);
        Assert.DoesNotContain("127.0.0.1", log);
    }
}
