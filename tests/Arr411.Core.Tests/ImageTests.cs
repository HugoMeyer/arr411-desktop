using System.Net;
using System.Security.Cryptography;
using System.Text;
using Arr411.Core.Hosting;
using Arr411.Core.Http;

namespace Arr411.Core.Tests;

public class ImageSnifferTests
{
    [Theory]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0 }, "image/png")]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0, 0 }, "image/jpeg")]
    [InlineData(new byte[] { 0x42, 0x4D, 0, 0, 0, 0 }, "image/bmp")]
    [InlineData(new byte[] { 0, 0, 1, 0, 1, 0 }, "image/x-icon")]
    public void Binary_formats_are_recognised_by_their_signature(byte[] data, string expected)
    {
        Assert.Equal(expected, ImageSniffer.Detect(data));
    }

    [Theory]
    [InlineData("GIF87a....", "image/gif")]
    [InlineData("GIF89a....", "image/gif")]
    [InlineData("RIFF\0\0\0\0WEBPVP8 ", "image/webp")]
    [InlineData("\0\0\0\u001cftypavif....", "image/avif")]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"></svg>", "image/svg+xml")]
    [InlineData("\uFEFF  \r\n<?xml version=\"1.0\"?><SVG></SVG>", "image/svg+xml")]
    public void Text_signatures_are_recognised(string head, string expected)
    {
        Assert.Equal(expected, ImageSniffer.Detect(Encoding.UTF8.GetBytes(head)));
    }

    [Theory]
    [InlineData("<html><body>login</body></html>")]
    [InlineData("{\"error\":\"nope\"}")]
    [InlineData("")]
    [InlineData("RIFF\0\0\0\0WAVEfmt ")]
    public void Anything_else_is_not_an_image(string head)
    {
        Assert.Null(ImageSniffer.Detect(Encoding.UTF8.GetBytes(head)));
    }

    [Fact]
    public void A_truncated_header_is_not_an_image_and_does_not_throw()
    {
        Assert.Null(ImageSniffer.Detect([0x52, 0x49]));
        Assert.Null(ImageSniffer.Detect([]));
    }
}

public sealed class ImageProxyTests : IDisposable
{
    private const string Base = "http://radarr.test:7878";
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3];

    private readonly TempDir _dir = new();
    private readonly FakeConfig _config = new() { ["radarrUrl"] = Base + "/", ["radarrKey"] = " key-1 " };
    private Func<RecordedRequest, HttpResponseMessage> _upstream = _ => StubHandler.Reply(HttpStatusCode.OK, Jpeg, "image/jpeg");
    private readonly StubHandler _handler;
    private readonly ImageProxy _proxy;

    public ImageProxyTests()
    {
        _handler = new StubHandler(request => _upstream(request));
        _proxy = new ImageProxy(_config, _dir.File("covers"), _handler);
    }

    public void Dispose()
    {
        _proxy.Dispose();
        _handler.Dispose();
        _dir.Dispose();
    }

    private static string Poster(int id = 1) => $"{Base}/MediaCover/{id}/poster.jpg";

    private static byte[] Read(HostResponse response)
    {
        using var buffer = new MemoryStream();
        response.Body.CopyTo(buffer);
        return buffer.ToArray();
    }

    private string CacheFile(string url) => Path.Combine(_dir.File("covers"), Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(url))));

    [Theory]
    [InlineData(null, "http://x/")]
    [InlineData("radarr", null)]
    [InlineData("other", "http://radarr.test:7878/a")]
    [InlineData("", "http://radarr.test:7878/a")]
    public async Task Missing_or_unknown_parameters_are_a_bad_request(string? service, string? target)
    {
        var response = await _proxy.GetAsync(service, target);

        Assert.Equal(400, response.Status);
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task An_unconfigured_service_is_forbidden()
    {
        var response = await _proxy.GetAsync("sonarr", "http://sonarr.test:8989/MediaCover/1/p.jpg");

        Assert.Equal(403, response.Status);
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task A_service_without_a_key_is_forbidden()
    {
        _config["radarrKey"] = "  ";

        Assert.Equal(403, (await _proxy.GetAsync("radarr", Poster())).Status);
    }

    [Theory]
    [InlineData("http://evil.test/MediaCover/1/p.jpg")]
    [InlineData("http://radarr.test:7878.evil.test/p.jpg")]
    [InlineData("http://radarr.test:7878")]
    [InlineData("https://radarr.test:7878/p.jpg")]
    public async Task Only_addresses_under_the_configured_service_are_fetched(string target)
    {
        var response = await _proxy.GetAsync("radarr", target);

        Assert.Equal(403, response.Status);
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task An_image_is_downloaded_with_the_api_key_and_served_with_a_sniffed_type()
    {
        var response = await _proxy.GetAsync("radarr", Poster());

        Assert.Equal(200, response.Status);
        Assert.Equal("image/jpeg", response.ContentType);
        Assert.Equal(Jpeg, Read(response));
        Assert.Equal("private, max-age=600", response.Headers["Cache-Control"]);
        Assert.Contains("sandbox", response.Headers["Content-Security-Policy"]);
        var sent = Assert.Single(_handler.Requests);
        Assert.Equal("key-1", sent.Header("X-Api-Key"));
        Assert.Equal(Poster(), sent.Target);
    }

    [Fact]
    public async Task The_cache_file_is_named_after_a_hash_of_the_url_not_the_url_itself()
    {
        await _proxy.GetAsync("radarr", Poster());

        Assert.True(File.Exists(CacheFile(Poster())));
        Assert.All(Directory.GetFiles(_dir.File("covers")), f => Assert.Matches("^[0-9a-f]{64}$", Path.GetFileName(f)));
    }

    [Fact]
    public async Task A_cached_image_is_served_without_going_to_the_network()
    {
        await _proxy.GetAsync("radarr", Poster());
        _upstream = _ => throw new InvalidOperationException("network must not be used");

        var second = await _proxy.GetAsync("radarr", Poster());

        Assert.Equal(200, second.Status);
        Assert.Equal(Jpeg, Read(second));
        Assert.Single(_handler.Requests);
    }

    [Fact]
    public async Task Different_images_do_not_share_a_cache_entry()
    {
        await _proxy.GetAsync("radarr", Poster(1));
        await _proxy.GetAsync("radarr", Poster(2));

        Assert.Equal(2, _handler.Requests.Count);
    }

    [Fact]
    public async Task An_image_older_than_a_week_is_downloaded_again()
    {
        await _proxy.GetAsync("radarr", Poster());
        File.SetLastWriteTimeUtc(CacheFile(Poster()), DateTime.UtcNow - TimeSpan.FromDays(8));
        var newer = new byte[] { 0xFF, 0xD8, 0xFF, 9, 9 };
        _upstream = _ => StubHandler.Reply(HttpStatusCode.OK, newer, "image/jpeg");

        var response = await _proxy.GetAsync("radarr", Poster());

        Assert.Equal(newer, Read(response));
        Assert.Equal(2, _handler.Requests.Count);
    }

    [Fact]
    public async Task An_outdated_copy_is_served_when_the_service_is_unreachable()
    {
        await _proxy.GetAsync("radarr", Poster());
        File.SetLastWriteTimeUtc(CacheFile(Poster()), DateTime.UtcNow - TimeSpan.FromDays(30));
        _upstream = _ => throw new HttpRequestException("offline");

        var response = await _proxy.GetAsync("radarr", Poster());

        Assert.Equal(200, response.Status);
        Assert.Equal(Jpeg, Read(response));
    }

    [Fact]
    public async Task An_unreachable_service_without_a_cached_copy_is_not_found()
    {
        _upstream = _ => throw new HttpRequestException("offline");

        Assert.Equal(404, (await _proxy.GetAsync("radarr", Poster())).Status);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.NoContent)]
    public async Task Only_a_200_is_accepted(HttpStatusCode status)
    {
        _upstream = _ => StubHandler.Reply(status, Jpeg, "image/jpeg");

        Assert.Equal(404, (await _proxy.GetAsync("radarr", Poster())).Status);
        Assert.False(File.Exists(CacheFile(Poster())));
    }

    [Fact]
    public async Task A_page_that_is_not_an_image_is_rejected_and_not_cached()
    {
        _upstream = _ => StubHandler.Reply(HttpStatusCode.OK, "<html>login</html>"u8.ToArray(), "text/html");

        Assert.Equal(404, (await _proxy.GetAsync("radarr", Poster())).Status);
        Assert.False(File.Exists(CacheFile(Poster())));
    }

    [Fact]
    public async Task A_missing_content_type_is_tolerated()
    {
        _upstream = _ => StubHandler.Reply(HttpStatusCode.OK, Jpeg);

        Assert.Equal(200, (await _proxy.GetAsync("radarr", Poster())).Status);
    }

    [Fact]
    public async Task An_empty_body_is_rejected()
    {
        _upstream = _ => StubHandler.Reply(HttpStatusCode.OK, [], "image/jpeg");

        Assert.Equal(404, (await _proxy.GetAsync("radarr", Poster())).Status);
    }

    [Fact]
    public async Task An_image_over_15_mb_is_rejected()
    {
        _upstream = _ => StubHandler.Reply(HttpStatusCode.OK, new byte[(15 * 1024 * 1024) + 1], "image/jpeg");

        Assert.Equal(404, (await _proxy.GetAsync("radarr", Poster())).Status);
    }

    [Fact]
    public async Task A_redirect_on_the_same_host_is_followed_with_the_key()
    {
        _upstream = request => request.Target.EndsWith("/poster.jpg", StringComparison.Ordinal)
            ? StubHandler.Reply(HttpStatusCode.Redirect, location: "/other/real.jpg")
            : StubHandler.Reply(HttpStatusCode.OK, Jpeg, "image/jpeg");

        var response = await _proxy.GetAsync("radarr", Poster());

        Assert.Equal(200, response.Status);
        Assert.Equal(2, _handler.Requests.Count);
        Assert.All(_handler.Requests, r => Assert.Equal("key-1", r.Header("X-Api-Key")));
    }

    [Fact]
    public async Task A_redirect_to_another_host_is_not_followed_so_the_key_never_leaves()
    {
        _upstream = _ => StubHandler.Reply(HttpStatusCode.Redirect, location: "http://evil.test/steal.jpg");

        var response = await _proxy.GetAsync("radarr", Poster());

        Assert.Equal(404, response.Status);
        Assert.Single(_handler.Requests);
    }

    [Fact]
    public async Task A_redirect_from_https_to_http_is_not_followed_so_the_key_never_travels_in_the_clear()
    {
        const string secure = "https://radarr.test:7878";
        _config["radarrUrl"] = secure;
        _upstream = _ => StubHandler.Reply(HttpStatusCode.Redirect, location: Base + "/MediaCover/1/poster.jpg");

        var response = await _proxy.GetAsync("radarr", secure + "/MediaCover/1/poster.jpg");

        Assert.Equal(404, response.Status);
        Assert.Single(_handler.Requests);
    }

    [Fact]
    public async Task A_redirect_from_http_to_https_on_the_same_host_is_followed_with_the_key()
    {
        _upstream = request => request.Target.StartsWith("https:", StringComparison.Ordinal)
            ? StubHandler.Reply(HttpStatusCode.OK, Jpeg, "image/jpeg")
            : StubHandler.Reply(HttpStatusCode.Redirect, location: "https://radarr.test:7878/MediaCover/1/poster.jpg");

        var response = await _proxy.GetAsync("radarr", Poster());

        Assert.Equal(200, response.Status);
        Assert.Equal(2, _handler.Requests.Count);
        Assert.All(_handler.Requests, r => Assert.Equal("key-1", r.Header("X-Api-Key")));
    }

    [Fact]
    public async Task A_redirect_to_another_service_of_the_host_is_not_followed_so_the_key_never_goes_there()
    {
        _upstream = _ => StubHandler.Reply(HttpStatusCode.Redirect, location: "http://radarr.test:9999/MediaCover/1/poster.jpg");

        var response = await _proxy.GetAsync("radarr", Poster());

        Assert.Equal(404, response.Status);
        Assert.Single(_handler.Requests);
    }

    [Fact]
    public async Task A_redirect_from_http_to_https_on_another_port_of_the_host_is_followed_with_the_key()
    {
        _upstream = request => request.Target.StartsWith("https:", StringComparison.Ordinal)
            ? StubHandler.Reply(HttpStatusCode.OK, Jpeg, "image/jpeg")
            : StubHandler.Reply(HttpStatusCode.Redirect, location: "https://radarr.test:7443/MediaCover/1/poster.jpg");

        var response = await _proxy.GetAsync("radarr", Poster());

        Assert.Equal(200, response.Status);
        Assert.Equal(2, _handler.Requests.Count);
        Assert.All(_handler.Requests, r => Assert.Equal("key-1", r.Header("X-Api-Key")));
    }

    [Fact]
    public async Task A_redirect_loop_gives_up()
    {
        _upstream = _ => StubHandler.Reply(HttpStatusCode.Redirect, location: "/again.jpg");

        Assert.Equal(404, (await _proxy.GetAsync("radarr", Poster())).Status);
        Assert.Equal(4, _handler.Requests.Count);
    }

    [Fact]
    public async Task A_redirect_to_a_non_web_scheme_is_refused()
    {
        _upstream = _ => StubHandler.Reply(HttpStatusCode.Redirect, location: "file:///C:/secret.jpg");

        Assert.Equal(404, (await _proxy.GetAsync("radarr", Poster())).Status);
    }

    [Fact]
    public async Task The_sniffed_type_wins_over_the_extension_of_the_url()
    {
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0];
        _upstream = _ => StubHandler.Reply(HttpStatusCode.OK, png, "image/png");

        var response = await _proxy.GetAsync("radarr", $"{Base}/MediaCover/1/poster.jpg");

        Assert.Equal("image/png", response.ContentType);
    }

    [Fact]
    public async Task The_type_falls_back_to_the_extension_when_the_bytes_are_unknown()
    {
        _upstream = _ => StubHandler.Reply(HttpStatusCode.OK, "mystery"u8.ToArray(), "image/png");

        Assert.Equal("image/png", (await _proxy.GetAsync("radarr", $"{Base}/MediaCover/1/p.png")).ContentType);
        Assert.Equal("application/octet-stream", (await _proxy.GetAsync("radarr", $"{Base}/MediaCover/1/p.bin")).ContentType);
    }

    [Fact]
    public async Task An_svg_is_served_as_an_image_with_a_sandboxing_policy()
    {
        _upstream = _ => StubHandler.Reply(HttpStatusCode.OK, "<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>"u8.ToArray(), "image/svg+xml");

        var response = await _proxy.GetAsync("radarr", Poster());

        Assert.Equal("image/svg+xml", response.ContentType);
        Assert.Contains("default-src 'none'", response.Headers["Content-Security-Policy"]);
    }

    [Fact]
    public async Task Many_parallel_requests_for_different_images_all_succeed()
    {
        var responses = await Task.WhenAll(Enumerable.Range(1, 30).Select(i => _proxy.GetAsync("radarr", Poster(i))));

        Assert.All(responses, r => Assert.Equal(200, r.Status));
        Assert.Equal(30, _handler.Requests.Count);
    }

    [Fact]
    public void Pruning_removes_temporary_files_and_the_oldest_images_first()
    {
        var folder = _dir.File("covers");
        Directory.CreateDirectory(folder);
        var now = DateTime.UtcNow;
        for (var i = 0; i < 5; i++)
        {
            var file = Path.Combine(folder, $"img{i}");
            File.WriteAllBytes(file, new byte[100]);
            File.SetLastWriteTimeUtc(file, now - TimeSpan.FromHours(10 - i));
        }

        File.WriteAllBytes(Path.Combine(folder, "img9.tmpabc"), new byte[100]);

        _proxy.Prune(threshold: 300, target: 200);

        var left = Directory.GetFiles(folder).Select(f => Path.GetFileName(f)).Order().ToArray();
        Assert.Equal(["img4"], left);
    }

    [Fact]
    public void Pruning_keeps_everything_below_the_threshold()
    {
        var folder = _dir.File("covers");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "a"), new byte[100]);
        File.WriteAllBytes(Path.Combine(folder, "b.tmp1"), new byte[100]);

        _proxy.Prune(threshold: 1000, target: 500);

        Assert.True(File.Exists(Path.Combine(folder, "a")));
        Assert.False(File.Exists(Path.Combine(folder, "b.tmp1")));
    }

    [Fact]
    public void Pruning_a_missing_folder_is_fine()
    {
        _proxy.Prune();
    }
}
