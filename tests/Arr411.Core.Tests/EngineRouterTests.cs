using System.Security.Cryptography;
using System.Text;
using Arr411.Core.Hosting;

namespace Arr411.Core.Tests;

public sealed class EngineRouterTests
{
    private const string Page =
        "<!doctype html>\r\n<html><head><meta charset=\"utf-8\"></head>\r\n<body>\r\n"
        + "  <script src=\"shim-engine.js\"></script>\r\n  <script src=\"background.js\"></script>\r\n"
        + "  <script>QbtEngine.ready();</script>\r\n</body></html>\r\n";

    private readonly Dictionary<string, string> _files = new()
    {
        ["engine.html"] = Page,
        ["shim-engine.js"] = "window.shim = 1;",
        ["background.js"] = "window.background = 1;",
        ["secret.txt"] = "secret",
        ["index.html"] = "<html>the web app</html>",
    };

    private readonly EngineRouter _router;
    private int _stateCalls;

    public EngineRouterTests()
    {
        _router = new EngineRouter(Store(_files), () =>
        {
            _stateCalls++;
            return """{"config":"{}"}""";
        });
    }

    private static AssetStore Store(Dictionary<string, string> files) =>
        new(name => files.TryGetValue(name, out var text) ? new MemoryStream(Encoding.UTF8.GetBytes(text)) : null);

    private Task<HostResponse> Get(string url, string method = "GET") => _router.HandleAsync(method, new Uri(url));

    private static string Text(HostResponse response)
    {
        using var reader = new StreamReader(response.Body, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    [Theory]
    [InlineData("https://engine.arr411.local/")]
    [InlineData("https://engine.arr411.local/engine.html")]
    [InlineData("https://ENGINE.arr411.local/engine.html?v=3#x")]
    public async Task The_engine_page_is_served_with_a_policy_that_allows_only_its_own_inline_script(string url)
    {
        var response = await Get(url);

        Assert.Equal(200, response.Status);
        Assert.Equal("text/html; charset=UTF-8", response.ContentType);
        Assert.Equal(Page, Text(response));
        var script = Convert.ToBase64String(SHA256.HashData("QbtEngine.ready();"u8));
        var policy = response.Headers["Content-Security-Policy"];
        Assert.Equal(
            $"default-src 'none'; script-src 'self' 'sha256-{script}'; connect-src 'self'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'",
            policy);
        Assert.DoesNotContain("unsafe", policy, StringComparison.Ordinal);
        Assert.Equal("nosniff", response.Headers["X-Content-Type-Options"]);
    }

    [Theory]
    [InlineData("/shim-engine.js", "window.shim = 1;")]
    [InlineData("/background.js", "window.background = 1;")]
    public async Task The_scripts_of_the_engine_are_served_but_never_sniffed(string path, string content)
    {
        var response = await Get("https://engine.arr411.local" + path);

        Assert.Equal(200, response.Status);
        Assert.Equal("application/javascript; charset=UTF-8", response.ContentType);
        Assert.Equal(content, Text(response));
        Assert.False(response.Headers.ContainsKey("Content-Security-Policy"));
        Assert.Equal("nosniff", response.Headers["X-Content-Type-Options"]);
    }

    [Theory]
    [InlineData("https://engine.arr411.local/index.html")]
    [InlineData("https://engine.arr411.local/app/index.html")]
    [InlineData("https://engine.arr411.local/secret.txt")]
    [InlineData("https://engine.arr411.local/%2e%2e/secret.txt")]
    [InlineData("https://engine.arr411.local/..%2Fsecret.txt")]
    [InlineData("https://engine.arr411.local/engine.html/")]
    [InlineData("https://engine.arr411.local/native/state/more")]
    [InlineData("https://engine.arr411.local/mc?s=radarr&u=x")]
    [InlineData("https://engine.arr411.local/background.js.map")]
    public async Task Nothing_else_is_served(string url)
    {
        var response = await Get(url);

        Assert.Equal(404, response.Status);
        Assert.Equal("", Text(response));
    }

    [Theory]
    [InlineData("https://arr411.local/engine.html")]
    [InlineData("https://engine.arr411.local.evil.test/engine.html")]
    [InlineData("https://evil.test/engine.html")]
    [InlineData("https://xengine.arr411.local/engine.html")]
    public async Task Another_site_is_not_served(string url)
    {
        Assert.Equal(404, (await Get(url)).Status);
    }

    [Fact]
    public async Task The_state_is_built_for_each_request_and_never_cached()
    {
        var first = await Get("https://engine.arr411.local/native/state");
        var second = await Get("https://engine.arr411.local/native/state");

        Assert.Equal(200, first.Status);
        Assert.Equal("application/json; charset=UTF-8", first.ContentType);
        Assert.Equal("no-store", first.Headers["Cache-Control"]);
        Assert.Equal("""{"config":"{}"}""", Text(first));
        Assert.Equal(200, second.Status);
        Assert.Equal(2, _stateCalls);
    }

    [Fact]
    public async Task The_favicon_the_web_view_asks_for_gets_no_content_rather_than_an_error()
    {
        Assert.Equal(204, (await Get("https://engine.arr411.local/favicon.ico")).Status);
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    [InlineData("PATCH")]
    public async Task Only_reading_is_allowed(string method)
    {
        var page = await Get("https://engine.arr411.local/engine.html", method);
        var state = await Get("https://engine.arr411.local/native/state", method);

        Assert.Equal(405, page.Status);
        Assert.Equal("GET, HEAD", page.Headers["Allow"]);
        Assert.Equal(405, state.Status);
        Assert.Equal(0, _stateCalls);
    }

    [Fact]
    public async Task A_head_request_gets_the_headers_without_the_body()
    {
        var response = await Get("https://engine.arr411.local/engine.html", "HEAD");

        Assert.Equal(200, response.Status);
        Assert.Equal("", Text(response));
        Assert.Contains("script-src 'self'", response.Headers["Content-Security-Policy"]);
    }

    [Fact]
    public void An_engine_without_its_page_is_reported_at_once()
    {
        Assert.Throws<InvalidOperationException>(() => new EngineRouter(Store([]), () => "{}"));
    }
}

public sealed class InlineScriptsTests
{
    private static string HashOf(string code) => "'sha256-" + Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(code))) + "'";

    [Fact]
    public void An_inline_script_is_allowed_by_its_hash()
    {
        string[] expected = [HashOf("QbtEngine.ready();")];

        Assert.Equal(expected, InlineScripts.Hashes("<body><script>QbtEngine.ready();</script></body>"));
    }

    [Theory]
    [InlineData("<script src=\"a.js\"></script>")]
    [InlineData("<script src=a.js></script>")]
    [InlineData("<SCRIPT SRC=\"a.js\">ignored</SCRIPT>")]
    [InlineData("<script type=\"module\" src = \"a.js\"></script>")]
    [InlineData("<script></script>")]
    [InlineData("<scriptx>code</scriptx>")]
    [InlineData("<p>no script here</p>")]
    public void Scripts_loaded_from_a_file_and_empty_ones_need_no_hash(string html)
    {
        Assert.Empty(InlineScripts.Hashes(html));
    }

    [Fact]
    public void Every_inline_script_gets_its_own_hash_and_duplicates_collapse()
    {
        var hashes = InlineScripts.Hashes("<script>a()</script><script src=\"x.js\"></script><script type=\"module\">b()</script><script>a()</script>");

        string[] expected = [HashOf("a()"), HashOf("b()")];
        Assert.Equal(expected, hashes);
    }

    [Fact]
    public void Line_breaks_count_as_the_browser_parses_them()
    {
        string[] expected = [HashOf("a();\nb();")];

        Assert.Equal(expected, InlineScripts.Hashes("<script>a();\r\nb();</script>"));
        Assert.Equal(expected, InlineScripts.Hashes("<script>a();\rb();</script>"));
    }

    [Fact]
    public void Whitespace_around_the_code_is_part_of_the_script()
    {
        string[] expected = [HashOf("\n  a();\n")];

        Assert.Equal(expected, InlineScripts.Hashes("<script>\n  a();\n</script>"));
    }
}
