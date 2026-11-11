using System.Net;
using System.Net.Sockets;
using Arr411.Core.Http;

namespace Arr411.Core.Tests;

/// <summary>A body that is not announced (no seeking, so no Content-Length): <c>length</c> bytes, then the end or, for the timeout tests, silence.</summary>
internal sealed class GeneratedStream(long length, bool stallAtEnd = false) : Stream
{
    private long _left = length;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) => Take(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer) => Take(buffer);

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_left == 0 && stallAtEnd) await Task.Delay(Timeout.Infinite, cancellationToken);
        return Take(buffer.Span);
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    private int Take(Span<byte> buffer)
    {
        var count = (int)Math.Min(buffer.Length, _left);
        buffer[..count].Fill(0xAB);
        _left -= count;
        return count;
    }
}

public class PageFetcherTests
{
    private const string UserAgent = "Test-Agent/1.0";
    private const string TorrentUrl = "https://c411.org/dl/1";

    private static Task<string?> NoCookies(Uri uri) => Task.FromResult<string?>(null);

    private static Task<PageFetchResult> FetchAsync(
        PageFetcher fetcher, string? url, string? referer = null, Func<Uri, Task<string?>>? cookies = null, CancellationToken cancellation = default) =>
        fetcher.FetchAsync(url, referer, UserAgent, cookies ?? NoCookies, cancellation);

    private static HttpResponseMessage Ok(params byte[] body) => StubHandler.Reply(HttpStatusCode.OK, body);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a url")]
    [InlineData("/dl/1")]
    [InlineData("//c411.org/dl/1")]
    [InlineData("http://c411.org/dl/1")]
    [InlineData("https://c411.org:8443/dl/1")]
    [InlineData("https://c411.org:80/dl/1")]
    [InlineData("https://user@c411.org/dl/1")]
    [InlineData("https://user:pass@c411.org/dl/1")]
    [InlineData("https://example.com/dl/1")]
    [InlineData("https://c411.org.evil.example/dl/1")]
    [InlineData("https://c411.org@evil.example/dl/1")]
    [InlineData("https://notc411.org/dl/1")]
    [InlineData("https://127.0.0.1/dl/1")]
    [InlineData("https://192.168.1.10/dl/1")]
    [InlineData("https://[::1]/dl/1")]
    [InlineData("ftp://c411.org/dl/1")]
    [InlineData("file:///C:/Windows/win.ini")]
    [InlineData("javascript:alert(1)")]
    public async Task An_address_that_is_not_C411_over_https_is_refused_without_any_request(string? url)
    {
        var handler = new StubHandler(_ => Ok(1));
        using var fetcher = new PageFetcher(handler);

        var result = await FetchAsync(fetcher, url);

        Assert.Equal("URL non supportée", result.Error);
        Assert.Equal(0, result.Status);
        Assert.Empty(result.Body);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("https://c411.org/dl/1")]
    [InlineData("HTTPS://C411.ORG/dl/1")]
    [InlineData("  https://c411.org/dl/1  ")]
    public async Task A_file_of_C411_is_downloaded_with_the_headers_of_the_browser(string url)
    {
        var handler = new StubHandler(_ => StubHandler.Reply(HttpStatusCode.OK, [1, 2, 3], "application/x-bittorrent"));
        using var fetcher = new PageFetcher(handler);

        var result = await FetchAsync(fetcher, url);

        Assert.Null(result.Error);
        Assert.Equal(200, result.Status);
        Expect.Equal([1, 2, 3], result.Body);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("GET", request.Method);
        Assert.Equal(TorrentUrl, request.Target);
        Assert.Equal(UserAgent, request.Header("User-Agent"));
        Assert.Equal("*/*", request.Header("Accept"));
        Assert.Null(request.Header("Cookie"));
        Assert.Null(request.Header("Referer"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public async Task A_missing_user_agent_is_replaced_by_a_generic_one(string userAgent)
    {
        var handler = new StubHandler(_ => Ok(1));
        using var fetcher = new PageFetcher(handler);

        await fetcher.FetchAsync(TorrentUrl, null, userAgent, NoCookies);

        Assert.Equal("Mozilla/5.0", Assert.Single(handler.Requests).Header("User-Agent"));
    }

    [Fact]
    public async Task The_cookies_of_the_C411_tab_go_with_the_request()
    {
        var handler = new StubHandler(_ => Ok(1));
        using var fetcher = new PageFetcher(handler);
        var asked = new List<Uri>();

        await FetchAsync(fetcher, TorrentUrl, cookies: uri =>
        {
            asked.Add(uri);
            return Task.FromResult<string?>("sid=abc; theme=dark");
        });

        Assert.Equal("sid=abc; theme=dark", Assert.Single(handler.Requests).Header("Cookie"));
        Assert.Equal(TorrentUrl, Assert.Single(asked).AbsoluteUri);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task No_cookie_header_is_sent_when_the_tab_has_none(string? cookie)
    {
        var handler = new StubHandler(_ => Ok(1));
        using var fetcher = new PageFetcher(handler);

        await FetchAsync(fetcher, TorrentUrl, cookies: _ => Task.FromResult(cookie));

        Assert.Null(Assert.Single(handler.Requests).Header("Cookie"));
    }

    [Fact]
    public async Task A_cookie_store_that_throws_does_not_stop_the_download()
    {
        var handler = new StubHandler(_ => Ok(1));
        using var fetcher = new PageFetcher(handler);

        var result = await FetchAsync(fetcher, TorrentUrl, cookies: _ => throw new InvalidOperationException("browser gone"));

        Assert.Null(result.Error);
        Assert.Null(Assert.Single(handler.Requests).Header("Cookie"));
    }

    [Fact]
    public async Task A_cookie_store_that_fails_later_does_not_stop_the_download()
    {
        var handler = new StubHandler(_ => Ok(1));
        using var fetcher = new PageFetcher(handler);

        var result = await FetchAsync(fetcher, TorrentUrl, cookies: _ => Task.FromException<string?>(new InvalidOperationException("browser gone")));

        Assert.Null(result.Error);
        Assert.Null(Assert.Single(handler.Requests).Header("Cookie"));
    }

    [Theory]
    [InlineData("https://c411.org/torrents/abc", "https://c411.org/torrents/abc")]
    [InlineData("https://c411.org/torrents/abc#comments", "https://c411.org/torrents/abc")]
    [InlineData("https://c411.org/search?q=a%20b&page=2", "https://c411.org/search?q=a%20b&page=2")]
    [InlineData("https://c411.org/", "https://c411.org/")]
    [InlineData("https://www.c411.org/torrents/abc", "https://www.c411.org/")]
    [InlineData("https://www.c411.org/torrents/abc?x=1", "https://www.c411.org/")]
    public async Task The_referer_is_the_page_for_its_own_site_and_the_origin_for_another_one(string page, string expected)
    {
        var handler = new StubHandler(_ => Ok(1));
        using var fetcher = new PageFetcher(handler);

        await FetchAsync(fetcher, TorrentUrl, page);

        Assert.Equal(expected, Assert.Single(handler.Requests).Header("Referer"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("http://c411.org/torrents/abc")]
    [InlineData("https://evil.example/torrents/abc")]
    [InlineData("https://c411.org.evil.example/")]
    [InlineData("https://user@c411.org/torrents/abc")]
    [InlineData("about:blank")]
    public async Task A_page_that_is_not_C411_over_https_is_not_given_as_referer(string? page)
    {
        var handler = new StubHandler(_ => Ok(1));
        using var fetcher = new PageFetcher(handler);

        await FetchAsync(fetcher, TorrentUrl, page);

        Assert.Null(Assert.Single(handler.Requests).Header("Referer"));
    }

    [Fact]
    public async Task A_redirect_to_a_CDN_is_followed_without_the_cookies_of_C411()
    {
        var handler = new StubHandler(request => request.Target.StartsWith("https://c411.org/", StringComparison.Ordinal)
            ? StubHandler.Reply(HttpStatusCode.Found, location: "https://cdn.example.net/files/1.torrent")
            : Ok(9));
        using var fetcher = new PageFetcher(handler);
        var asked = new List<string>();

        var result = await FetchAsync(fetcher, TorrentUrl, "https://c411.org/torrents/abc", uri =>
        {
            asked.Add(uri.Host);
            return Task.FromResult<string?>("sid=abc");
        });

        Assert.Equal(200, result.Status);
        Expect.Equal([9], result.Body);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("sid=abc", handler.Requests[0].Header("Cookie"));
        Assert.Equal("https://c411.org/torrents/abc", handler.Requests[0].Header("Referer"));
        Assert.Equal("https://cdn.example.net/files/1.torrent", handler.Requests[1].Target);
        Assert.Null(handler.Requests[1].Header("Cookie"));
        Assert.Equal("https://c411.org/", handler.Requests[1].Header("Referer"));
        Assert.Equal("c411.org", Assert.Single(asked));
    }

    [Fact]
    public async Task A_relative_redirect_stays_on_the_same_server()
    {
        var handler = new StubHandler(request => request.Target == TorrentUrl
            ? StubHandler.Reply(HttpStatusCode.Found, location: "/files/1.torrent")
            : Ok(5));
        using var fetcher = new PageFetcher(handler);

        var result = await FetchAsync(fetcher, TorrentUrl, cookies: _ => Task.FromResult<string?>("sid=abc"));

        Assert.Equal(200, result.Status);
        Assert.Equal(["https://c411.org/dl/1", "https://c411.org/files/1.torrent"], handler.Requests.Select(r => r.Target));
        Assert.All(handler.Requests, r => Assert.Equal("sid=abc", r.Header("Cookie")));
    }

    [Fact]
    public async Task A_redirect_to_another_host_of_C411_asks_for_the_cookies_of_that_host()
    {
        var handler = new StubHandler(request => request.Target == TorrentUrl
            ? StubHandler.Reply(HttpStatusCode.MovedPermanently, location: "https://www.c411.org/dl/2")
            : Ok(5));
        using var fetcher = new PageFetcher(handler);

        await FetchAsync(fetcher, TorrentUrl, cookies: uri => Task.FromResult<string?>("host=" + uri.Host));

        Assert.Equal("host=c411.org", handler.Requests[0].Header("Cookie"));
        Assert.Equal("host=www.c411.org", handler.Requests[1].Header("Cookie"));
    }

    [Theory]
    [InlineData(301)]
    [InlineData(302)]
    [InlineData(303)]
    [InlineData(307)]
    [InlineData(308)]
    public async Task Every_kind_of_redirect_is_followed(int status)
    {
        var handler = new StubHandler(request => request.Target == TorrentUrl
            ? StubHandler.Reply((HttpStatusCode)status, location: "/next")
            : Ok(5));
        using var fetcher = new PageFetcher(handler);

        var result = await FetchAsync(fetcher, TorrentUrl);

        Assert.Equal(200, result.Status);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Theory]
    [InlineData("http://cdn.example.net/1.torrent")]
    [InlineData("https://cdn.example.net:8443/1.torrent")]
    [InlineData("https://user@cdn.example.net/1.torrent")]
    [InlineData("https://127.0.0.1/1.torrent")]
    [InlineData("https://10.0.0.5/1.torrent")]
    [InlineData("https://192.168.1.1/1.torrent")]
    [InlineData("https://172.16.0.1/1.torrent")]
    [InlineData("https://169.254.169.254/latest/meta-data/")]
    [InlineData("https://[::1]/1.torrent")]
    [InlineData("https://[fe80::1]/1.torrent")]
    [InlineData("https://[fd00::1]/1.torrent")]
    [InlineData("https://[::ffff:10.0.0.1]/1.torrent")]
    [InlineData("https://localhost/1.torrent")]
    [InlineData("https://nas/1.torrent")]
    [InlineData("https://router.lan/1.torrent")]
    [InlineData("https://printer.local/1.torrent")]
    [InlineData("https://2130706433/1.torrent")]
    [InlineData("https://0x7f000001/1.torrent")]
    [InlineData("https://127.1/1.torrent")]
    [InlineData("ftp://cdn.example.net/1.torrent")]
    [InlineData("file:///C:/Windows/win.ini")]
    public async Task A_redirect_that_leaves_the_Internet_or_the_rules_is_refused_before_any_request(string location)
    {
        var handler = new StubHandler(_ => StubHandler.Reply(HttpStatusCode.Found, location: location));
        using var fetcher = new PageFetcher(handler);

        var result = await FetchAsync(fetcher, TorrentUrl);

        Assert.Equal("Redirection refusée", result.Error);
        Assert.Equal(0, result.Status);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task A_redirect_without_a_destination_is_the_answer()
    {
        var handler = new StubHandler(_ => StubHandler.Reply(HttpStatusCode.MovedPermanently, [4, 2]));
        using var fetcher = new PageFetcher(handler);

        var result = await FetchAsync(fetcher, TorrentUrl);

        Assert.Null(result.Error);
        Assert.Equal(301, result.Status);
        Expect.Equal([4, 2], result.Body);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Five_redirects_are_followed_and_the_sixth_request_is_the_last()
    {
        var served = 0;
        var handler = new StubHandler(_ => Interlocked.Increment(ref served) <= 5
            ? StubHandler.Reply(HttpStatusCode.Found, location: "/step/" + served)
            : Ok(7));
        using var fetcher = new PageFetcher(handler);

        var result = await FetchAsync(fetcher, TorrentUrl);

        Assert.Null(result.Error);
        Assert.Equal(200, result.Status);
        Assert.Equal(PageFetcher.MaxRequests, handler.Requests.Count);
    }

    [Fact]
    public async Task A_loop_of_redirects_ends_after_six_requests()
    {
        var handler = new StubHandler(_ => StubHandler.Reply(HttpStatusCode.Found, location: "/again"));
        using var fetcher = new PageFetcher(handler);

        var result = await FetchAsync(fetcher, TorrentUrl);

        Assert.Equal("Trop de redirections", result.Error);
        Assert.Equal(PageFetcher.MaxRequests, handler.Requests.Count);
    }

    [Theory]
    [InlineData(404)]
    [InlineData(403)]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(503)]
    public async Task The_status_of_the_server_is_passed_on_with_its_body(int status)
    {
        var handler = new StubHandler(_ => StubHandler.Reply((HttpStatusCode)status, [0x6e, 0x6f]));
        using var fetcher = new PageFetcher(handler);

        var result = await FetchAsync(fetcher, TorrentUrl);

        Assert.Null(result.Error);
        Assert.Equal(status, result.Status);
        Expect.Equal([0x6e, 0x6f], result.Body);
    }

    [Fact]
    public async Task A_body_of_exactly_the_limit_is_accepted()
    {
        var handler = new StubHandler(_ => StubHandler.Reply(HttpStatusCode.OK, new byte[PageFetcher.MaxBodyBytes]));
        using var fetcher = new PageFetcher(handler);

        var result = await FetchAsync(fetcher, TorrentUrl);

        Assert.Null(result.Error);
        Assert.Equal(PageFetcher.MaxBodyBytes, result.Body.Length);
    }

    [Fact]
    public async Task A_body_over_the_limit_is_refused_from_its_announced_length()
    {
        var handler = new StubHandler(_ => StubHandler.Reply(HttpStatusCode.OK, new byte[PageFetcher.MaxBodyBytes + 1]));
        using var fetcher = new PageFetcher(handler);

        var result = await FetchAsync(fetcher, TorrentUrl);

        Assert.Equal("Fichier trop volumineux", result.Error);
        Assert.Equal(0, result.Status);
        Assert.Empty(result.Body);
    }

    [Fact]
    public async Task A_body_over_the_limit_is_refused_even_when_no_length_is_announced()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new GeneratedStream(PageFetcher.MaxBodyBytes + 1)) });
        using var fetcher = new PageFetcher(handler);

        var result = await FetchAsync(fetcher, TorrentUrl);

        Assert.Equal("Fichier trop volumineux", result.Error);
        Assert.Empty(result.Body);
    }

    [Fact]
    public async Task A_body_of_unknown_length_within_the_limit_is_accepted()
    {
        const int Size = 100 * 1024 + 5;
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new GeneratedStream(Size)) });
        using var fetcher = new PageFetcher(handler);

        var result = await FetchAsync(fetcher, TorrentUrl);

        Assert.Null(result.Error);
        Assert.Equal(Size, result.Body.Length);
        Assert.All(result.Body, b => Assert.Equal(0xAB, b));
    }

    [Fact]
    public async Task A_server_that_never_answers_ends_with_a_timeout()
    {
        var handler = new StubHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return Ok(1);
        });
        using var fetcher = new PageFetcher(handler) { ReadTimeout = TimeSpan.FromMilliseconds(100) };

        var result = await FetchAsync(fetcher, TorrentUrl);

        Assert.Equal("Réseau : délai dépassé", result.Error);
        Assert.Equal(0, result.Status);
    }

    [Fact]
    public async Task A_body_that_stops_flowing_ends_with_a_timeout()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new GeneratedStream(10, stallAtEnd: true)) });
        using var fetcher = new PageFetcher(handler) { ReadTimeout = TimeSpan.FromMilliseconds(200) };

        var result = await FetchAsync(fetcher, TorrentUrl);

        Assert.Equal("Réseau : délai dépassé", result.Error);
        Assert.Empty(result.Body);
    }

    [Fact]
    public async Task A_cancelled_download_says_so()
    {
        var started = new TaskCompletionSource();
        var handler = new StubHandler(async (_, token) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return Ok(1);
        });
        using var fetcher = new PageFetcher(handler);
        using var cancel = new CancellationTokenSource();

        var pending = FetchAsync(fetcher, TorrentUrl, cancellation: cancel.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await cancel.CancelAsync();

        var result = await pending.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("Réseau : requête annulée", result.Error);
    }

    [Fact]
    public async Task A_download_that_was_cancelled_before_it_started_says_so()
    {
        var handler = new StubHandler(_ => Ok(1));
        using var fetcher = new PageFetcher(handler);
        using var cancel = new CancellationTokenSource();
        await cancel.CancelAsync();

        var result = await FetchAsync(fetcher, TorrentUrl, cancellation: cancel.Token);

        Assert.Equal("Réseau : requête annulée", result.Error);
    }

    [Fact]
    public async Task A_network_failure_is_reported_not_thrown()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException(HttpRequestError.NameResolutionError, "no such host"));
        using var fetcher = new PageFetcher(handler);

        var result = await FetchAsync(fetcher, TorrentUrl);

        Assert.Equal("Réseau : serveur introuvable (no such host)", result.Error);
        Assert.Equal(0, result.Status);
        Assert.Empty(result.Body);
    }

    [Fact]
    public async Task A_refused_connection_is_reported_not_thrown()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException(HttpRequestError.ConnectionError, "refused"));
        using var fetcher = new PageFetcher(handler);

        var result = await FetchAsync(fetcher, TorrentUrl);

        Assert.StartsWith("Réseau : connexion impossible", result.Error);
    }

    [Fact]
    public void The_real_handler_can_be_created_and_disposed()
    {
        using var fetcher = new PageFetcher();

        Assert.Equal(PageFetcher.DefaultReadTimeout, TimeSpan.FromSeconds(30));
    }
}

public class PageFetcherGuardTests
{
    [Theory]
    [InlineData("https://c411.org/x", true, true)]
    [InlineData("https://www.c411.org/x", true, true)]
    [InlineData("https://cdn.example.net/x", true, false)]
    [InlineData("https://cdn.example.net/x", false, true)]
    [InlineData("https://c411.org/x", false, true)]
    [InlineData("http://c411.org/x", false, false)]
    [InlineData("http://cdn.example.net/x", false, false)]
    [InlineData("https://c411.org:8443/x", false, false)]
    [InlineData("https://cdn.example.net:444/x", false, false)]
    [InlineData("https://user@cdn.example.net/x", false, false)]
    [InlineData("https://user:pass@cdn.example.net/x", false, false)]
    [InlineData("https://10.0.0.1/x", false, false)]
    [InlineData("https://nas/x", false, false)]
    [InlineData("https://8.8.8.8/x", false, true)]
    [InlineData("https://8.8.8.8/x", true, false)]
    public void Where_a_request_may_go(string url, bool first, bool expected)
    {
        Assert.Equal(expected, PageFetcher.IsAllowedTarget(new Uri(url), first));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void An_address_that_is_not_absolute_is_never_allowed(bool first)
    {
        Assert.False(PageFetcher.IsAllowedTarget(new Uri("/dl/1", UriKind.Relative), first));
    }

    [Theory]
    [InlineData("https://c411.org/")]
    [InlineData("https://www.c411.org/x")]
    [InlineData("https://cdn.example.net/")]
    [InlineData("https://example.com./")]
    [InlineData("https://a.b.c.example.org/")]
    [InlineData("https://8.8.8.8/")]
    [InlineData("https://[2606:4700:4700::1111]/")]
    [InlineData("https://home.example.com/")]
    public void A_server_of_the_Internet_is_public(string url)
    {
        Assert.True(PageFetcher.IsPublicHost(new Uri(url)));
    }

    [Theory]
    [InlineData("https://localhost/")]
    [InlineData("https://nas/")]
    [InlineData("https://router/")]
    [InlineData("https://host.localhost/")]
    [InlineData("https://router.lan/")]
    [InlineData("https://printer.local/")]
    [InlineData("https://a.b.internal/")]
    [InlineData("https://x.home/")]
    [InlineData("https://x.corp/")]
    [InlineData("https://x.intranet/")]
    [InlineData("https://x.private/")]
    [InlineData("https://x.localdomain/")]
    [InlineData("https://printer.home.arpa/")]
    [InlineData("https://NAS.LAN/")]
    [InlineData("https://127.0.0.1/")]
    [InlineData("https://10.0.0.1/")]
    [InlineData("https://192.168.0.1/")]
    [InlineData("https://169.254.169.254/")]
    [InlineData("https://0.0.0.0/")]
    [InlineData("https://[::1]/")]
    [InlineData("https://[::]/")]
    [InlineData("https://[fe80::1]/")]
    [InlineData("https://[fd00::1]/")]
    [InlineData("https://[::ffff:127.0.0.1]/")]
    [InlineData("https://2130706433/")]
    [InlineData("https://0x7f000001/")]
    [InlineData("https://0177.0.0.1/")]
    [InlineData("https://127.1/")]
    [InlineData("https://0/")]
    public void A_server_of_a_private_network_is_not_public(string url)
    {
        Assert.False(PageFetcher.IsPublicHost(new Uri(url)));
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("172.15.255.255")]
    [InlineData("172.32.0.1")]
    [InlineData("100.63.255.255")]
    [InlineData("100.128.0.1")]
    [InlineData("169.253.1.1")]
    [InlineData("198.17.0.1")]
    [InlineData("198.20.0.1")]
    [InlineData("192.0.1.1")]
    [InlineData("192.0.3.1")]
    [InlineData("203.0.112.1")]
    [InlineData("203.0.114.1")]
    [InlineData("223.255.255.255")]
    [InlineData("2606:4700:4700::1111")]
    [InlineData("2001:4860:4860::8888")]
    [InlineData("2001:200::1")]
    [InlineData("::ffff:8.8.8.8")]
    [InlineData("64:ff9b::808:808")]
    [InlineData("2002:0808:0808::")]
    public void An_address_of_the_Internet_is_public(string address)
    {
        Assert.True(PageFetcher.IsPublicAddress(IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("0.1.2.3")]
    [InlineData("10.1.2.3")]
    [InlineData("10.255.255.255")]
    [InlineData("127.0.0.1")]
    [InlineData("127.255.255.254")]
    [InlineData("100.64.0.1")]
    [InlineData("100.127.255.255")]
    [InlineData("169.254.0.1")]
    [InlineData("169.254.169.254")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.0.1")]
    [InlineData("192.168.255.255")]
    [InlineData("192.0.0.1")]
    [InlineData("192.0.2.1")]
    [InlineData("192.88.99.1")]
    [InlineData("198.18.0.1")]
    [InlineData("198.19.255.255")]
    [InlineData("198.51.100.1")]
    [InlineData("203.0.113.1")]
    [InlineData("224.0.0.1")]
    [InlineData("239.255.255.250")]
    [InlineData("240.0.0.1")]
    [InlineData("255.255.255.255")]
    [InlineData("::")]
    [InlineData("::1")]
    [InlineData("fe80::1")]
    [InlineData("fc00::1")]
    [InlineData("fd12:3456:789a::1")]
    [InlineData("ff02::1")]
    [InlineData("::ffff:10.0.0.1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("::ffff:192.168.1.1")]
    [InlineData("64:ff9b::a00:1")]
    [InlineData("2002:0a00:0001::")]
    [InlineData("2002:7f00:0001::")]
    [InlineData("2001::1")]
    [InlineData("2001:1::1")]
    [InlineData("2001:db8::1")]
    [InlineData("3fff::1")]
    public void An_address_of_a_private_or_reserved_range_is_not_public(string address)
    {
        Assert.False(PageFetcher.IsPublicAddress(IPAddress.Parse(address)));
    }

    [Fact]
    public async Task A_name_that_resolves_to_private_addresses_is_not_connected_to()
    {
        var resolved = 0;

        await Assert.ThrowsAsync<HttpRequestException>(async () => await PageFetcher.ConnectGuardedAsync(
            new DnsEndPoint("files.example.net", 443),
            (_, _) =>
            {
                resolved++;
                return Task.FromResult(new[] { IPAddress.Parse("10.0.0.1"), IPAddress.Parse("192.168.0.2"), IPAddress.Loopback });
            },
            PageFetcher.IsPublicAddress,
            CancellationToken.None));

        Assert.Equal(1, resolved);
    }

    [Theory]
    [InlineData("10.1.2.3")]
    [InlineData("127.0.0.1")]
    [InlineData("169.254.169.254")]
    [InlineData("::1")]
    public async Task A_literal_private_address_is_refused_without_asking_a_resolver(string host)
    {
        await Assert.ThrowsAsync<HttpRequestException>(async () => await PageFetcher.ConnectGuardedAsync(
            new DnsEndPoint(host, 443),
            (_, _) => throw new InvalidOperationException("must not resolve a literal address"),
            PageFetcher.IsPublicAddress,
            CancellationToken.None));
    }

    [Fact]
    public async Task Only_the_addresses_that_pass_the_filter_are_tried()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var accepted = listener.AcceptTcpClientAsync();

        await using var stream = await PageFetcher.ConnectGuardedAsync(
            new DnsEndPoint("files.example.net", port),
            (_, _) => Task.FromResult(new[] { IPAddress.Parse("10.255.255.1"), IPAddress.Loopback }),
            IPAddress.IsLoopback,
            CancellationToken.None);
        using var peer = await accepted.WaitAsync(TimeSpan.FromSeconds(10));

        stream.WriteByte(7);
        await stream.FlushAsync();
        Assert.Equal(7, peer.GetStream().ReadByte());
    }

    [Fact]
    public async Task A_literal_address_that_passes_the_filter_is_connected_to_without_a_resolver()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var accepted = listener.AcceptTcpClientAsync();

        await using var stream = await PageFetcher.ConnectGuardedAsync(
            new DnsEndPoint("127.0.0.1", port),
            (_, _) => throw new InvalidOperationException("must not resolve a literal address"),
            IPAddress.IsLoopback,
            CancellationToken.None);
        using var peer = await accepted.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(peer.Connected);
    }

    [Fact]
    public async Task A_connection_that_was_cancelled_is_not_made()
    {
        using var cancel = new CancellationTokenSource();
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await PageFetcher.ConnectGuardedAsync(
            new DnsEndPoint("198.51.100.7", 443),
            (_, _) => throw new InvalidOperationException("must not resolve a literal address"),
            _ => true,
            cancel.Token));
    }
}
