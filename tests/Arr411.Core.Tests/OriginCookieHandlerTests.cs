using System.Net;
using Arr411.Core.Http;

namespace Arr411.Core.Tests;

public class OriginCookieHandlerTests
{
    private static HttpResponseMessage SetsACookie(string cookie = "SID=1; path=/")
    {
        var response = StubHandler.Reply(HttpStatusCode.OK);
        response.Headers.TryAddWithoutValidation("Set-Cookie", cookie);
        return response;
    }

    private sealed class Rig : IDisposable
    {
        private readonly StubHandler _stub;
        private readonly HttpClient _client;

        public Rig(Func<RecordedRequest, HttpResponseMessage> respond, int maxOrigins = OriginCookieHandler.DefaultMaxOrigins)
        {
            _stub = new StubHandler(respond);
            Handler = new OriginCookieHandler(_stub, maxOrigins);
            _client = new HttpClient(Handler);
        }

        public OriginCookieHandler Handler { get; }

        /// <summary>Makes a request and says which cookies it carried.</summary>
        public async Task<string?> CookieSentTo(string url)
        {
            using var response = await _client.GetAsync(url);
            return _stub.Requests[^1].Header("Cookie");
        }

        public void Dispose() => _client.Dispose();
    }

    [Fact]
    public async Task The_scheme_the_host_and_the_port_each_have_their_own_cookies()
    {
        using var rig = new Rig(_ => SetsACookie());

        Assert.Null(await rig.CookieSentTo("http://nas.test:8080/"));
        Assert.Equal("SID=1", await rig.CookieSentTo("http://nas.test:8080/other"));
        Assert.Equal("SID=1", await rig.CookieSentTo("http://NAS.test:8080/"));
        Assert.Null(await rig.CookieSentTo("https://nas.test:8080/"));
        Assert.Null(await rig.CookieSentTo("http://nas.test:7878/"));
        Assert.Null(await rig.CookieSentTo("http://other.test:8080/"));
    }

    [Fact]
    public async Task The_oldest_server_makes_room_when_there_are_too_many()
    {
        using var rig = new Rig(_ => SetsACookie(), maxOrigins: 3);

        foreach (var host in new[] { "s1", "s2", "s3", "s4" }) await rig.CookieSentTo($"http://{host}.test/");

        Assert.Equal(3, rig.Handler.OriginCount);
        Assert.Equal("SID=1", await rig.CookieSentTo("http://s4.test/"));
        Assert.Equal("SID=1", await rig.CookieSentTo("http://s3.test/"));
        Assert.Equal("SID=1", await rig.CookieSentTo("http://s2.test/"));
        Assert.Null(await rig.CookieSentTo("http://s1.test/"));
        Assert.Equal(3, rig.Handler.OriginCount);
    }

    [Fact]
    public async Task A_server_that_sets_no_cookie_takes_no_place()
    {
        using var rig = new Rig(_ => StubHandler.Reply(HttpStatusCode.OK), maxOrigins: 3);

        for (var i = 0; i < 10; i++) await rig.CookieSentTo($"http://s{i}.test/");

        Assert.Equal(0, rig.Handler.OriginCount);
    }

    [Fact]
    public async Task A_cookie_for_another_domain_is_ignored_without_failing_the_request()
    {
        using var rig = new Rig(_ => SetsACookie("SID=1; domain=evil.example; path=/"));

        Assert.Null(await rig.CookieSentTo("http://nas.test/"));
        Assert.Null(await rig.CookieSentTo("http://nas.test/again"));
    }
}
