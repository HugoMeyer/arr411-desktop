using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Arr411.Core.Http;

namespace Arr411.Core.Tests;

public class RedirectRulesTests
{
    [Theory]
    [InlineData("http://nas:7878/a", "http://nas:7878/b", true)]
    [InlineData("http://NAS:7878/a", "http://nas:7878/b", true)]
    [InlineData("https://nas:7878/a", "https://nas:7878/b", true)]
    [InlineData("http://nas:7878/a", "https://nas:7878/a", true)]
    [InlineData("http://nas/a", "https://nas/a", true)]
    [InlineData("http://nas:8080/a", "https://nas:8443/a", true)]
    [InlineData("https://nas/a", "http://nas/a", false)]
    [InlineData("https://nas:7878/a", "http://nas:7878/a", false)]
    [InlineData("http://nas:7878/a", "http://other:7878/a", false)]
    [InlineData("http://nas:7878/a", "http://nas.evil.test:7878/a", false)]
    [InlineData("http://nas:7878/a", "http://nas@evil.test/a", false)]
    [InlineData("http://nas:7878/a", "http://nas:9999/a", false)]
    [InlineData("http://nas/a", "http://nas:8080/a", false)]
    [InlineData("https://nas:7443/a", "https://nas:8443/a", false)]
    [InlineData("http://nas:7878/a", "ftp://nas:7878/a", false)]
    [InlineData("http://nas:7878/a", "file:///C:/secret", false)]
    public void A_redirect_stays_on_the_server_only_when_it_goes_to_the_same_service(string from, string to, bool stays) =>
        Assert.Equal(stays, RedirectRules.StaysOnTheServer(new Uri(from), new Uri(to)));
}

public sealed class SameServerRedirectHandlerTests
{
    private const string Origin = "http://nas:7878";

    private static HttpResponseMessage Redirect(HttpStatusCode status, string location) => StubHandler.Reply(status, location: location);

    private static HttpResponseMessage Final() => StubHandler.Reply(HttpStatusCode.OK, "final"u8.ToArray(), "text/plain");

    private static string[] Targets(StubHandler stub) => [.. stub.Requests.Select(request => request.Target)];

    private static HttpRequestMessage Post(string url, string body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body)) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-www-form-urlencoded");
        return request;
    }

    private static async Task<HttpResponseMessage> SendAsync(StubHandler stub, HttpRequestMessage request)
    {
        using var invoker = new HttpMessageInvoker(new SameServerRedirectHandler(stub));
        return await invoker.SendAsync(request, CancellationToken.None);
    }

    [Fact]
    public async Task A_redirect_on_the_server_is_followed_with_the_headers()
    {
        var stub = new StubHandler(request => request.Target.EndsWith("/a", StringComparison.Ordinal) ? Redirect(HttpStatusCode.Found, "/b") : Final());
        using var request = new HttpRequestMessage(HttpMethod.Get, Origin + "/a");
        request.Headers.TryAddWithoutValidation("X-Api-Key", "key-1");

        using var response = await SendAsync(stub, request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal([Origin + "/a", Origin + "/b"], Targets(stub));
        Assert.All(stub.Requests, seen => Assert.Equal("key-1", seen.Header("X-Api-Key")));
    }

    [Fact]
    public async Task A_redirect_to_another_machine_is_the_answer_and_nothing_goes_there()
    {
        var stub = new StubHandler(_ => Redirect(HttpStatusCode.TemporaryRedirect, "http://evil.test/steal"));
        using var request = Post(Origin + "/login", "username=admin&password=secret");
        request.Headers.TryAddWithoutValidation("X-Api-Key", "key-1");

        using var response = await SendAsync(stub, request);

        Assert.Equal(HttpStatusCode.TemporaryRedirect, response.StatusCode);
        Assert.Equal([Origin + "/login"], Targets(stub));
    }

    [Theory]
    [InlineData("http://nas:9999/steal")]
    [InlineData("http://nas.evil.test:7878/steal")]
    [InlineData("ftp://nas:7878/steal")]
    public async Task A_redirect_to_another_service_or_another_kind_of_address_is_the_answer(string location)
    {
        var stub = new StubHandler(_ => Redirect(HttpStatusCode.Found, location));
        using var request = new HttpRequestMessage(HttpMethod.Get, Origin + "/a");

        using var response = await SendAsync(stub, request);

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Single(stub.Requests);
    }

    [Fact]
    public async Task A_redirect_from_https_to_http_is_the_answer()
    {
        var stub = new StubHandler(_ => Redirect(HttpStatusCode.Found, "http://nas:7878/a"));
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://nas:7878/a");

        using var response = await SendAsync(stub, request);

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Single(stub.Requests);
    }

    [Fact]
    public async Task A_redirect_to_the_encrypted_twin_of_the_service_is_followed()
    {
        var stub = new StubHandler(request => request.Target.StartsWith("https:", StringComparison.Ordinal) ? Final() : Redirect(HttpStatusCode.MovedPermanently, "https://nas:7443/a"));
        using var request = new HttpRequestMessage(HttpMethod.Get, Origin + "/a");

        using var response = await SendAsync(stub, request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal([Origin + "/a", "https://nas:7443/a"], Targets(stub));
    }

    [Theory]
    [InlineData(HttpStatusCode.TemporaryRedirect)]
    [InlineData(HttpStatusCode.PermanentRedirect)]
    public async Task A_307_or_a_308_sends_the_same_request_again_with_its_body(HttpStatusCode status)
    {
        var stub = new StubHandler(request => request.Target.EndsWith("/a", StringComparison.Ordinal) ? Redirect(status, "/b") : Final());
        using var request = Post(Origin + "/a", "x=1");

        using var response = await SendAsync(stub, request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, stub.Requests.Count);
        Assert.All(stub.Requests, seen =>
        {
            Assert.Equal("POST", seen.Method);
            Assert.Equal("x=1", seen.BodyText);
            Assert.Equal("application/x-www-form-urlencoded", seen.Header("Content-Type"));
        });
    }

    [Theory]
    [InlineData(HttpStatusCode.MultipleChoices)]
    [InlineData(HttpStatusCode.MovedPermanently)]
    [InlineData(HttpStatusCode.Found)]
    [InlineData(HttpStatusCode.SeeOther)]
    public async Task A_post_that_is_redirected_comes_back_as_a_get_without_its_body(HttpStatusCode status)
    {
        var stub = new StubHandler(request => request.Target.EndsWith("/a", StringComparison.Ordinal) ? Redirect(status, "/b") : Final());
        using var request = Post(Origin + "/a", "x=1");

        using var response = await SendAsync(stub, request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var second = stub.Requests[1];
        Assert.Equal("GET", second.Method);
        Assert.Empty(second.Body);
        Assert.Null(second.Header("Content-Type"));
    }

    [Theory]
    [InlineData(HttpStatusCode.MovedPermanently, "PUT", "PUT")]
    [InlineData(HttpStatusCode.Found, "DELETE", "DELETE")]
    [InlineData(HttpStatusCode.SeeOther, "PUT", "GET")]
    [InlineData(HttpStatusCode.SeeOther, "DELETE", "GET")]
    [InlineData(HttpStatusCode.SeeOther, "GET", "GET")]
    [InlineData(HttpStatusCode.SeeOther, "HEAD", "HEAD")]
    public async Task Only_the_methods_of_forms_change_when_the_request_is_redirected(HttpStatusCode status, string method, string expected)
    {
        var stub = new StubHandler(request => request.Target.EndsWith("/a", StringComparison.Ordinal) ? Redirect(status, "/b") : Final());
        using var request = new HttpRequestMessage(new HttpMethod(method), Origin + "/a");

        using var response = await SendAsync(stub, request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(expected, stub.Requests[1].Method);
    }

    [Fact]
    public async Task An_authorization_is_not_sent_again()
    {
        var stub = new StubHandler(request => request.Target.EndsWith("/a", StringComparison.Ordinal) ? Redirect(HttpStatusCode.Found, "/b") : Final());
        using var request = new HttpRequestMessage(HttpMethod.Get, Origin + "/a");
        request.Headers.TryAddWithoutValidation("Authorization", "Basic YWRtaW46c2VjcmV0");

        using var response = await SendAsync(stub, request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Basic YWRtaW46c2VjcmV0", stub.Requests[0].Header("Authorization"));
        Assert.Null(stub.Requests[1].Header("Authorization"));
    }

    [Fact]
    public async Task A_status_that_is_not_a_redirect_is_the_answer_whatever_it_holds()
    {
        var stub = new StubHandler(_ => Redirect(HttpStatusCode.Created, "/new"));
        using var request = new HttpRequestMessage(HttpMethod.Get, Origin + "/a");

        using var response = await SendAsync(stub, request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Single(stub.Requests);
    }

    [Fact]
    public async Task A_redirect_without_a_destination_is_the_answer()
    {
        var stub = new StubHandler(_ => StubHandler.Reply(HttpStatusCode.Found));
        using var request = new HttpRequestMessage(HttpMethod.Get, Origin + "/a");

        using var response = await SendAsync(stub, request);

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Single(stub.Requests);
    }

    [Fact]
    public async Task Ten_redirects_are_followed()
    {
        var calls = 0;
        var stub = new StubHandler(_ => Interlocked.Increment(ref calls) <= SameServerRedirectHandler.MaxRedirects ? Redirect(HttpStatusCode.Found, "/next") : Final());
        using var request = new HttpRequestMessage(HttpMethod.Get, Origin + "/a");

        using var response = await SendAsync(stub, request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(SameServerRedirectHandler.MaxRedirects + 1, stub.Requests.Count);
    }

    [Fact]
    public async Task A_loop_of_redirects_ends_with_an_error_after_ten()
    {
        var stub = new StubHandler(_ => Redirect(HttpStatusCode.Found, "/again"));
        using var request = new HttpRequestMessage(HttpMethod.Get, Origin + "/a");

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => SendAsync(stub, request));

        Assert.Contains("redirections", error.Message, StringComparison.Ordinal);
        Assert.Equal(SameServerRedirectHandler.MaxRedirects + 1, stub.Requests.Count);
    }
}
