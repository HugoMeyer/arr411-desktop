using System.Net;
using System.Text;
using Arr411.Core.Hosting;
using Arr411.Core.Http;

namespace Arr411.Core.Tests;

public sealed class HostRouterTests : IDisposable
{
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3];

    private readonly TempDir _dir = new();
    private readonly FakeConfig _config = new() { ["radarrUrl"] = "http://radarr.test:7878", ["radarrKey"] = "k" };
    private readonly StubHandler _upstream = new(_ => StubHandler.Reply(HttpStatusCode.OK, Jpeg, "image/jpeg"));
    private readonly ImageProxy _covers;
    private readonly HostRouter _router;
    private int _stateCalls;

    public HostRouterTests()
    {
        var files = new Dictionary<string, string>
        {
            ["index.html"] = "<html>Arr411</html>",
            ["main.js"] = "console.log(1)",
            ["../secret.txt"] = "secret",
        };
        var app = new AssetStore(name => files.TryGetValue(name, out var text) ? new MemoryStream(Encoding.UTF8.GetBytes(text)) : null);
        _covers = new ImageProxy(_config, _dir.File("covers"), _upstream);
        _router = new HostRouter(app, _covers, () =>
        {
            _stateCalls++;
            return """{"tab":"c411"}""";
        });
    }

    public void Dispose()
    {
        _covers.Dispose();
        _upstream.Dispose();
        _dir.Dispose();
    }

    private Task<HostResponse> Get(string url, string method = "GET") => _router.HandleAsync(method, new Uri(url));

    private static string Text(HostResponse response)
    {
        using var reader = new StreamReader(response.Body, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    [Theory]
    [InlineData("https://arr411.local/app/")]
    [InlineData("https://arr411.local/app/index.html")]
    [InlineData("https://ARR411.local/app/index.html?v=3#x")]
    public async Task The_web_app_is_served_with_a_content_security_policy(string url)
    {
        var response = await Get(url);

        Assert.Equal(200, response.Status);
        Assert.Equal("text/html; charset=UTF-8", response.ContentType);
        Assert.Equal("<html>Arr411</html>", Text(response));
        Assert.Contains("script-src 'self'", response.Headers["Content-Security-Policy"]);
        Assert.Contains("frame-ancestors 'none'", response.Headers["Content-Security-Policy"]);
        Assert.Equal("nosniff", response.Headers["X-Content-Type-Options"]);
    }

    [Fact]
    public async Task Scripts_are_served_without_a_page_policy_but_never_sniffed()
    {
        var response = await Get("https://arr411.local/app/main.js");

        Assert.Equal(200, response.Status);
        Assert.Equal("application/javascript; charset=UTF-8", response.ContentType);
        Assert.False(response.Headers.ContainsKey("Content-Security-Policy"));
        Assert.Equal("nosniff", response.Headers["X-Content-Type-Options"]);
        Assert.Equal("no-cache", response.Headers["Cache-Control"]);
    }

    [Fact]
    public async Task The_favicon_the_web_view_asks_for_gets_no_content_rather_than_an_error()
    {
        var response = await Get("https://arr411.local/favicon.ico");

        Assert.Equal(204, response.Status);
        Assert.Equal(0, response.Body.Length);
        Assert.Equal("nosniff", response.Headers["X-Content-Type-Options"]);
    }

    [Theory]
    [InlineData("https://arr411.local/")]
    [InlineData("https://arr411.local/app")]
    [InlineData("https://arr411.local/other")]
    [InlineData("https://arr411.local/app/missing.js")]
    [InlineData("https://arr411.local/native/other")]
    [InlineData("https://elsewhere.test/app/index.html")]
    [InlineData("https://engine.arr411.local/app/index.html")]
    public async Task Everything_else_is_not_found(string url)
    {
        Assert.Equal(404, (await Get(url)).Status);
    }

    [Theory]
    [InlineData("https://arr411.local/app/..%2fsecret.txt")]
    [InlineData("https://arr411.local/app/..%5csecret.txt")]
    [InlineData("https://arr411.local/app/%2e%2e%2fsecret.txt")]
    [InlineData("https://arr411.local/app/../secret.txt")]
    public async Task Path_traversal_is_refused(string url)
    {
        Assert.Equal(404, (await Get(url)).Status);
    }

    [Fact]
    public async Task The_boot_state_is_served_fresh_every_time()
    {
        var first = await Get("https://arr411.local/native/state");
        await Get("https://arr411.local/native/state");

        Assert.Equal(200, first.Status);
        Assert.Equal("application/json; charset=UTF-8", first.ContentType);
        Assert.Equal("no-store", first.Headers["Cache-Control"]);
        Assert.Equal("""{"tab":"c411"}""", Text(first));
        Assert.Equal(2, _stateCalls);
    }

    [Fact]
    public async Task Covers_are_fetched_through_the_proxy()
    {
        var target = Uri.EscapeDataString("http://radarr.test:7878/MediaCover/1/poster.jpg");

        var response = await Get($"https://arr411.local/mc?s=radarr&u={target}");

        Assert.Equal(200, response.Status);
        Assert.Equal("image/jpeg", response.ContentType);
        Assert.Equal("nosniff", response.Headers["X-Content-Type-Options"]);
        Assert.Equal("private, max-age=600", response.Headers["Cache-Control"]);
        Assert.Equal("k", Assert.Single(_upstream.Requests).Header("X-Api-Key"));
    }

    [Theory]
    [InlineData("https://arr411.local/mc", 400)]
    [InlineData("https://arr411.local/mc?s=radarr", 400)]
    [InlineData("https://arr411.local/mc?s=bogus&u=http%3A%2F%2Fradarr.test%3A7878%2Fx", 400)]
    [InlineData("https://arr411.local/mc?s=radarr&u=http%3A%2F%2Fevil.test%2Fx", 403)]
    [InlineData("https://arr411.local/mc?s=sonarr&u=http%3A%2F%2Fradarr.test%3A7878%2Fx", 403)]
    public async Task Cover_requests_are_validated(string url, int expected)
    {
        Assert.Equal(expected, (await Get(url)).Status);
        Assert.Empty(_upstream.Requests);
    }

    [Fact]
    public async Task A_head_request_gets_the_headers_and_no_body()
    {
        var response = await Get("https://arr411.local/app/index.html", "HEAD");

        Assert.Equal(200, response.Status);
        Assert.Equal("text/html; charset=UTF-8", response.ContentType);
        Assert.Equal(0, response.Body.Length);
        Assert.Contains("default-src", response.Headers["Content-Security-Policy"]);
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    [InlineData("OPTIONS")]
    public async Task Other_methods_are_not_allowed(string method)
    {
        var response = await Get("https://arr411.local/app/index.html", method);

        Assert.Equal(405, response.Status);
        Assert.Equal("GET, HEAD", response.Headers["Allow"]);
    }

    [Fact]
    public async Task The_method_is_case_insensitive()
    {
        Assert.Equal(200, (await Get("https://arr411.local/app/index.html", "get")).Status);
    }
}
