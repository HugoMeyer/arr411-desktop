using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Text.Json.Nodes;
using Arr411.Core.Http;

namespace Arr411.Core.Tests;

public class NativeHttpTests
{
    private static NativeHttp Client(TimeSpan? readTimeout = null) => new("Arr411-Test/1.0", readTimeout: readTimeout);

    private static string Request(string url, string? method = null, JsonObject? headers = null, JsonObject? body = null)
    {
        var request = new JsonObject { ["url"] = url };
        if (method is not null) request["method"] = method;
        if (headers is not null) request["headers"] = headers;
        if (body is not null) request["body"] = body;
        return request.ToJsonString();
    }

    private static JsonObject TextBody(string text) => new() { ["type"] = "text", ["text"] = text };

    [Fact]
    public async Task A_get_returns_the_status_and_the_utf8_body()
    {
        using var server = new TestHttpServer(_ => TestResponse.Text("héllo ✓"));
        using var http = Client();

        var result = await http.SendAsync(Request(server.Url("/api?x=1")));

        Assert.Equal(200, result.Status);
        Assert.Equal("héllo ✓", result.Text);
        Assert.Null(result.Error);
        var seen = Assert.Single(server.Requests);
        Assert.Equal("GET", seen.Method);
        Assert.Equal("/api?x=1", seen.Target);
    }

    [Fact]
    public async Task Origin_referer_and_user_agent_are_set_like_the_android_app_does()
    {
        using var server = new TestHttpServer();
        using var http = Client();

        await http.SendAsync(Request(server.Url("/x")));

        var seen = Assert.Single(server.Requests);
        Assert.Equal($"http://127.0.0.1:{server.Port}", seen.Header("Origin"));
        Assert.Equal($"http://127.0.0.1:{server.Port}/", seen.Header("Referer"));
        Assert.Equal("Arr411-Test/1.0", seen.Header("User-Agent"));
    }

    [Fact]
    public async Task Headers_of_the_request_override_the_defaults()
    {
        using var server = new TestHttpServer();
        using var http = Client();
        var headers = new JsonObject { ["origin"] = "http://other", ["User-Agent"] = "X/2", ["X-Test"] = "1" };

        await http.SendAsync(Request(server.Url(), headers: headers));

        var seen = Assert.Single(server.Requests);
        Assert.Equal("http://other", seen.Header("Origin"));
        Assert.Equal("X/2", seen.Header("User-Agent"));
        Assert.Equal("1", seen.Header("X-Test"));
    }

    [Fact]
    public async Task Error_statuses_still_return_their_body()
    {
        using var server = new TestHttpServer(_ => TestResponse.Text("denied", 403));
        using var http = Client();

        var result = await http.SendAsync(Request(server.Url()));

        Assert.Equal(403, result.Status);
        Assert.Equal("denied", result.Text);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task Text_bodies_are_form_encoded_unless_told_otherwise()
    {
        using var server = new TestHttpServer();
        using var http = Client();

        await http.SendAsync(Request(server.Url("/login"), "POST", body: TextBody("username=a&password=b%C3%A9é")));

        var seen = Assert.Single(server.Requests);
        Assert.Equal("POST", seen.Method);
        Assert.Equal("application/x-www-form-urlencoded", seen.Header("Content-Type"));
        Assert.Equal("username=a&password=b%C3%A9é", seen.BodyText);
    }

    [Fact]
    public async Task The_content_type_of_a_text_body_can_be_chosen()
    {
        using var server = new TestHttpServer();
        using var http = Client();
        var headers = new JsonObject { ["Content-Type"] = "application/json" };

        await http.SendAsync(Request(server.Url(), "POST", headers, TextBody("{\"a\":1}")));

        var seen = Assert.Single(server.Requests);
        Assert.Equal("application/json", seen.Header("Content-Type"));
        Assert.Equal("{\"a\":1}", seen.BodyText);
    }

    [Fact]
    public async Task Multipart_bodies_are_built_exactly_like_the_android_app_does()
    {
        using var server = new TestHttpServer();
        using var http = Client();
        var parts = JsonNode.Parse("""
            [{"name":"urls","value":"magnet:?xt=urn:btih:abc"},
             {"name":"torrents","filename":"a b.torrent","contentType":"application/x-bittorrent","base64":"AQID"},
             {"name":"raw","base64":"BA=="}]
            """);
        var body = new JsonObject { ["type"] = "multipart", ["parts"] = parts };

        await http.SendAsync(Request(server.Url("/add"), "POST", body: body));

        var seen = Assert.Single(server.Requests);
        var contentType = seen.Header("Content-Type")!;
        Assert.StartsWith("multipart/form-data; boundary=----c411qbt", contentType);
        var boundary = contentType["multipart/form-data; boundary=".Length..];

        var expected = new List<byte>();
        expected.AddRange(Encoding.UTF8.GetBytes(
            $"--{boundary}\r\nContent-Disposition: form-data; name=\"urls\"\r\n\r\nmagnet:?xt=urn:btih:abc\r\n"
            + $"--{boundary}\r\nContent-Disposition: form-data; name=\"torrents\"; filename=\"a b.torrent\"\r\nContent-Type: application/x-bittorrent\r\n\r\n"));
        expected.AddRange([1, 2, 3]);
        expected.AddRange(Encoding.UTF8.GetBytes(
            $"\r\n--{boundary}\r\nContent-Disposition: form-data; name=\"raw\"; filename=\"file\"\r\nContent-Type: application/octet-stream\r\n\r\n"));
        expected.Add(4);
        expected.AddRange(Encoding.UTF8.GetBytes($"\r\n--{boundary}--\r\n"));
        Assert.Equal([.. expected], seen.Body);
    }

    [Fact]
    public void Quotes_and_line_breaks_cannot_break_out_of_a_multipart_header()
    {
        var parts = JsonNode.Parse("""[{"name":"a\"\r\nX-Evil: 1","base64":"AA==","filename":"f\"\r\n.torrent","contentType":"x/y\r\nX-Evil: 2"}]""")!.AsArray();

        var (body, _) = Multipart.Build(parts);

        var text = Encoding.UTF8.GetString(body);
        Assert.DoesNotContain("\r\nX-Evil", text);
        Assert.Contains("name=\"aX-Evil: 1\"; filename=\"f.torrent\"", text);
    }

    [Fact]
    public async Task Cookies_are_kept_between_requests()
    {
        using var server = new TestHttpServer(request => request.Header("Cookie") is null
            ? TestResponse.Text("first", 200, ("Set-Cookie", "SID=abc123; path=/"))
            : TestResponse.Text("second"));
        using var http = Client();

        await http.SendAsync(Request(server.Url("/login")));
        var second = await http.SendAsync(Request(server.Url("/list")));

        Assert.Equal("second", second.Text);
        Assert.Equal("SID=abc123", server.Requests[1].Header("Cookie"));
    }

    [Fact]
    public async Task Compressed_responses_are_decoded()
    {
        using var server = new TestHttpServer(_ => TestResponse.Gzip("zipped ✓"));
        using var http = Client();

        var result = await http.SendAsync(Request(server.Url()));

        Assert.Equal("zipped ✓", result.Text);
    }

    [Fact]
    public async Task Redirects_are_followed()
    {
        using var server = new TestHttpServer(request => request.Target == "/a" ? TestResponse.Redirect("/b") : TestResponse.Text("final"));
        using var http = Client();

        var result = await http.SendAsync(Request(server.Url("/a")));

        Assert.Equal(200, result.Status);
        Assert.Equal("final", result.Text);
        Assert.Equal(2, server.Requests.Count);
    }

    [Fact]
    public async Task A_redirect_with_a_307_sends_the_headers_and_the_body_again_to_the_same_server()
    {
        using var server = new TestHttpServer(request => request.Target == "/a" ? new TestResponse(307, [], [("Location", "/b")]) : TestResponse.Text("final"));
        using var http = Client();
        var headers = new JsonObject { ["X-Api-Key"] = "key-1" };

        var result = await http.SendAsync(Request(server.Url("/a"), "POST", headers, TextBody("username=admin&password=secret")));

        Assert.Equal("final", result.Text);
        Assert.Equal(2, server.Requests.Count);
        Assert.All(server.Requests, seen =>
        {
            Assert.Equal("POST", seen.Method);
            Assert.Equal("username=admin&password=secret", seen.BodyText);
            Assert.Equal("key-1", seen.Header("X-Api-Key"));
        });
    }

    [Fact]
    public async Task A_post_redirected_with_a_302_comes_back_as_a_get_without_its_body()
    {
        using var server = new TestHttpServer(request => request.Target == "/a" ? TestResponse.Redirect("/b") : TestResponse.Text("final"));
        using var http = Client();

        var result = await http.SendAsync(Request(server.Url("/a"), "POST", body: TextBody("x=1")));

        Assert.Equal("final", result.Text);
        Assert.Equal("GET", server.Requests[1].Method);
        Assert.Empty(server.Requests[1].Body);
    }

    [Fact]
    public async Task A_redirect_to_another_service_is_the_answer_so_the_key_and_the_password_never_leave()
    {
        using var other = new TestHttpServer();
        using var server = new TestHttpServer(_ => new TestResponse(307, [], [("Location", other.Url("/steal"))]));
        using var http = Client();
        var headers = new JsonObject { ["X-Api-Key"] = "key-1" };

        var result = await http.SendAsync(Request(server.Url("/login"), "POST", headers, TextBody("username=admin&password=secret")));

        Assert.Equal(307, result.Status);
        Assert.Null(result.Error);
        Assert.Single(server.Requests);
        Assert.Empty(other.Requests);
    }

    [Fact]
    public async Task A_redirect_to_another_host_is_the_answer()
    {
        using var other = new TestHttpServer();
        using var server = new TestHttpServer(_ => TestResponse.Redirect($"http://localhost:{other.Port}/steal"));
        using var http = Client();

        var result = await http.SendAsync(Request(server.Url("/a"), headers: new JsonObject { ["X-Api-Key"] = "key-1" }));

        Assert.Equal(302, result.Status);
        Assert.Empty(other.Requests);
    }

    [Fact]
    public async Task A_loop_of_redirects_is_reported_in_french()
    {
        using var server = new TestHttpServer(_ => TestResponse.Redirect("/again"));
        using var http = Client();

        var result = await http.SendAsync(Request(server.Url("/a")));

        Assert.Equal(0, result.Status);
        Assert.Equal("Réseau : trop de redirections", result.Error);
        Assert.Equal(SameServerRedirectHandler.MaxRedirects + 1, server.Requests.Count);
    }

    [Theory]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    [InlineData("PATCH")]
    [InlineData("OPTIONS")]
    [InlineData("HEAD")]
    [InlineData("post")]
    public async Task The_usual_methods_are_supported(string method)
    {
        using var server = new TestHttpServer();
        using var http = Client();

        var result = await http.SendAsync(Request(server.Url(), method));

        Assert.Equal(200, result.Status);
        Assert.Equal(method.ToUpperInvariant(), Assert.Single(server.Requests).Method);
    }

    [Fact]
    public async Task Headers_managed_by_the_http_stack_are_ignored()
    {
        using var server = new TestHttpServer();
        using var http = Client();
        var headers = new JsonObject { ["Host"] = "evil.example", ["Content-Length"] = "3", ["Connection"] = "keep-alive" };

        await http.SendAsync(Request(server.Url(), "POST", headers, TextBody("abcdef")));

        var seen = Assert.Single(server.Requests);
        Assert.Equal($"127.0.0.1:{server.Port}", seen.Header("Host"));
        Assert.Equal("abcdef", seen.BodyText);
    }

    [Fact]
    public async Task A_silent_server_times_out()
    {
        using var server = new TestHttpServer(_ => TestResponse.Silent());
        using var http = Client(TimeSpan.FromMilliseconds(300));

        var result = await http.SendAsync(Request(server.Url()));

        Assert.Equal(0, result.Status);
        Assert.Equal("", result.Text);
        Assert.Equal("Réseau : délai dépassé", result.Error);
    }

    [Fact]
    public async Task A_refused_connection_is_reported_in_french()
    {
        int port;
        using (var closed = new TestHttpServer()) port = closed.Port;
        using var http = Client();

        var result = await http.SendAsync(Request($"http://127.0.0.1:{port}/"));

        Assert.Equal(0, result.Status);
        Assert.StartsWith("Réseau : connexion refusée (", result.Error);
    }

    [Fact]
    public async Task The_caller_can_cancel_a_request()
    {
        using var server = new TestHttpServer(_ => TestResponse.Silent());
        using var http = Client();
        using var cancellation = new CancellationTokenSource();

        var pending = http.SendAsync(Request(server.Url()), cancellation.Token);
        await Task.Delay(150);
        await cancellation.CancelAsync();
        var result = await pending;

        Assert.Equal("Réseau : requête annulée", result.Error);
    }

    [Theory]
    [InlineData("""{"url":"not a url"}""", "Adresse invalide : not a url")]
    [InlineData("""{"url":"file:///C:/Windows/win.ini"}""", "Adresse invalide : file:///C:/Windows/win.ini")]
    [InlineData("""{"url":"ftp://example.com/"}""", "Adresse invalide : ftp://example.com/")]
    [InlineData("""{"method":"GET"}""", "Adresse invalide : adresse vide")]
    [InlineData("""{"url":"http://127.0.0.1:1/","method":"TRACE"}""", "Réseau : Invalid HTTP method: TRACE")]
    public async Task Invalid_requests_are_reported_without_touching_the_network(string request, string expected)
    {
        using var http = Client();

        var result = await http.SendAsync(request);

        Assert.Equal(0, result.Status);
        Assert.Equal(expected, result.Error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    public async Task Requests_that_are_not_json_objects_fail_cleanly(string request)
    {
        using var http = Client();

        var result = await http.SendAsync(request);

        Assert.Equal(0, result.Status);
        Assert.StartsWith("Réseau : ", result.Error);
    }

    [Fact]
    public async Task A_fake_network_stack_can_replace_the_real_one()
    {
        using var handler = new StubHandler(_ => StubHandler.Reply(HttpStatusCode.Accepted, "ok"u8.ToArray()));
        using var http = new NativeHttp("UA", handler);

        var result = await http.SendAsync("""{"url":"http://radarr.test:7878/api/v3/system/status"}""");

        Assert.Equal(202, result.Status);
        Assert.Equal("ok", result.Text);
    }

    [Fact]
    public void Network_failures_get_the_same_messages_as_on_android()
    {
        static HttpRequestException Wrap(HttpRequestError kind, Exception inner) => new(kind, "boom", inner);

        Assert.Equal(
            "Réseau : serveur introuvable (boom)",
            NativeHttp.Describe(Wrap(HttpRequestError.NameResolutionError, new SocketException((int)SocketError.HostNotFound))));
        Assert.Equal(
            "Réseau : serveur introuvable (boom)",
            NativeHttp.Describe(Wrap(HttpRequestError.NameResolutionError, new SocketException((int)SocketError.TryAgain))));
        Assert.Equal(
            "Réseau : connexion refusée (boom)",
            NativeHttp.Describe(Wrap(HttpRequestError.ConnectionError, new SocketException((int)SocketError.ConnectionRefused))));
        Assert.Equal(
            "Réseau : délai dépassé",
            NativeHttp.Describe(Wrap(HttpRequestError.ConnectionError, new SocketException((int)SocketError.TimedOut))));
        Assert.Equal("Réseau : délai dépassé", NativeHttp.Describe(new TimeoutException("slow")));
        Assert.Equal(
            "Réseau : erreur SSL / certificat (boom)",
            NativeHttp.Describe(Wrap(HttpRequestError.SecureConnectionError, new AuthenticationException("bad certificate"))));
        Assert.Equal("Adresse invalide : bad", NativeHttp.Describe(new UriFormatException("bad")));
        Assert.Equal("Réseau : connexion impossible (boom)", NativeHttp.Describe(Wrap(HttpRequestError.ConnectionError, new IOException("x"))));
        Assert.Equal("Réseau : something", NativeHttp.Describe(new InvalidOperationException("something")));
    }
}
