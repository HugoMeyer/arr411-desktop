using System.Buffers;
using System.Net;
using System.Net.Sockets;
using Arr411.Core.Logging;
using Arr411.Core.Shell;

namespace Arr411.Core.Http;

/// <summary>What <see cref="PageFetcher"/> got: the answer of the server whatever its status, or the reason it got none.</summary>
public sealed record PageFetchResult(int Status, byte[] Body, string? Error)
{
    public static PageFetchResult Fail(string error) => new(0, [], error);
}

/// <summary>
/// Downloads a file for the C411 page when the page's own fetch is refused (CORS): the Android app's PageBridge.fetchBytes.
/// The request carries the cookies of the C411 tab, so it may only go where a browser tab on C411 would go: to public
/// servers over HTTPS, never to the user's own network (a page must not use the PC as a springboard).
/// </summary>
public sealed class PageFetcher : IDisposable
{
    public const int MaxBodyBytes = 16 * 1024 * 1024;
    public const int MaxRequests = 6;

    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan DefaultReadTimeout = TimeSpan.FromSeconds(30);

    private const string FallbackUserAgent = "Mozilla/5.0";

    // Names that only mean something on a private network.
    private static readonly string[] LocalSuffixes =
    [
        ".localhost", ".local", ".localdomain", ".lan", ".home", ".internal", ".intranet", ".corp", ".private", ".home.arpa",
    ];

    private readonly HttpClient _client;

    /// <param name="handler">Replaces the real network stack (tests). Must not follow redirects by itself.</param>
    public PageFetcher(HttpMessageHandler? handler = null) =>
        _client = new HttpClient(handler ?? CreateHandler()) { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>How long a request may wait for the answer and, once it flows, for each piece of data. Settable for the tests only.</summary>
    internal TimeSpan ReadTimeout { get; init; } = DefaultReadTimeout;

    public void Dispose() => _client.Dispose();

    /// <summary>
    /// GETs the file at <paramref name="url"/>, following up to <see cref="MaxRequests"/> - 1 redirects by hand so that every step is checked.
    /// Never throws for network problems: they come back as <see cref="PageFetchResult.Error"/>.
    /// </summary>
    /// <param name="url">Must be an https address of c411.org.</param>
    /// <param name="referer">The page asking; only sent when it is itself a page of c411.org.</param>
    /// <param name="userAgent">The one of the C411 tab, so that the server sees the same browser.</param>
    /// <param name="cookies">The Cookie header the C411 tab would send to this address (null when it has none).</param>
    public async Task<PageFetchResult> FetchAsync(
        string? url, string? referer, string userAgent, Func<Uri, Task<string?>> cookies, CancellationToken cancellation = default)
    {
        try
        {
            if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var current) || !IsAllowedTarget(current, first: true))
                return PageFetchResult.Fail("URL non supportée");

            var origin = RefererPage(referer);
            for (var request = 0; request < MaxRequests; request++)
            {
                using var message = new HttpRequestMessage(HttpMethod.Get, current);
                message.Headers.TryAddWithoutValidation("User-Agent", string.IsNullOrWhiteSpace(userAgent) ? FallbackUserAgent : userAgent);
                message.Headers.TryAddWithoutValidation("Accept", "*/*");

                var from = RefererFor(origin, current);
                if (from is not null) message.Headers.TryAddWithoutValidation("Referer", from);

                // Cookies of C411 stay with C411: an address elsewhere (a CDN a redirect leads to) gets none.
                if (ExternalLinks.IsC411Host(current.Host))
                {
                    var cookie = await CookiesOrNullAsync(cookies, current).ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(cookie)) message.Headers.TryAddWithoutValidation("Cookie", cookie);
                }

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                timeout.CancelAfter(ReadTimeout);

                using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                if (IsRedirect(response.StatusCode) && response.Headers.Location is { } location)
                {
                    var next = location.IsAbsoluteUri ? location : new Uri(current, location);
                    if (!IsAllowedTarget(next, first: false)) return PageFetchResult.Fail("Redirection refusée");

                    current = next;
                    continue;
                }

                var body = await ReadBodyAsync(response, timeout).ConfigureAwait(false);
                return body is null ? PageFetchResult.Fail("Fichier trop volumineux") : new PageFetchResult((int)response.StatusCode, body, null);
            }

            return PageFetchResult.Fail("Trop de redirections");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return PageFetchResult.Fail("Réseau : requête annulée");
        }
        catch (OperationCanceledException)
        {
            return PageFetchResult.Fail("Réseau : délai dépassé");
        }
        catch (Exception ex)
        {
            return PageFetchResult.Fail(NativeHttp.Describe(ex));
        }
    }

    /// <summary>Whether the address may be requested: https on the standard port, no credentials, a public server, and C411 for the first one.</summary>
    internal static bool IsAllowedTarget(Uri uri, bool first) =>
        uri.IsAbsoluteUri
        && uri.Scheme == Uri.UriSchemeHttps
        && uri.Port == 443
        && uri.UserInfo.Length == 0
        && (!first || ExternalLinks.IsC411Host(uri.IdnHost))
        && IsPublicHost(uri);

    /// <summary>Whether the host can only be a server of the Internet: not an address of a private range, not a name of a local network.</summary>
    internal static bool IsPublicHost(Uri uri)
    {
        switch (uri.HostNameType)
        {
            case UriHostNameType.IPv4:
            case UriHostNameType.IPv6:
                return IPAddress.TryParse(uri.DnsSafeHost, out var address) && IsPublicAddress(address);
            case UriHostNameType.Dns:
                var host = uri.IdnHost.TrimEnd('.').ToLowerInvariant();
                if (!host.Contains('.')) return false;
                foreach (var suffix in LocalSuffixes)
                {
                    if (host.EndsWith(suffix, StringComparison.Ordinal)) return false;
                }

                return true;
            default:
                return false;
        }
    }

    /// <summary>Whether the address belongs to the public Internet (not loopback, private, link-local, shared, reserved, multicast...).</summary>
    internal static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();

        var b = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork) return IsPublicV4(b[0], b[1], b[2]);
        if (address.AddressFamily != AddressFamily.InterNetworkV6) return false;

        // NAT64 (64:ff9b::/96) and 6to4 (2002::/16) carry an IPv4 address, which is the one that counts.
        if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xff && b[3] == 0x9b && IsZero(b, 4, 8)) return IsPublicV4(b[12], b[13], b[14]);
        if ((b[0] & 0xe0) != 0x20) return false;
        if (b[0] == 0x20 && b[1] == 0x02) return IsPublicV4(b[2], b[3], b[4]);

        // 2001::/23 (Teredo and other protocol assignments), 2001:db8::/32 and 3fff::/20 (documentation).
        if (b[0] == 0x20 && b[1] == 0x01 && (b[2] & 0xfe) == 0x00) return false;
        if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0d && b[3] == 0xb8) return false;
        return !(b[0] == 0x3f && b[1] == 0xff && (b[2] & 0xf0) == 0x00);
    }

    private static bool IsPublicV4(byte a, byte b, byte c) => a switch
    {
        0 or 10 or 127 => false,
        100 => b is < 64 or > 127,
        169 => b != 254,
        172 => b is < 16 or > 31,
        192 => !(b == 168 || (b == 0 && c is 0 or 2) || (b == 88 && c == 99)),
        198 => !(b is 18 or 19 || (b == 51 && c == 100)),
        203 => !(b == 0 && c == 113),
        >= 224 => false,
        _ => true,
    };

    private static bool IsZero(byte[] bytes, int start, int count)
    {
        for (var i = start; i < start + count; i++)
        {
            if (bytes[i] != 0) return false;
        }

        return true;
    }

    /// <summary>The connection step of the real handler: only public addresses are ever connected to, whatever the name resolves to.</summary>
    internal static async ValueTask<Stream> ConnectGuardedAsync(
        DnsEndPoint endpoint,
        Func<string, CancellationToken, Task<IPAddress[]>> resolve,
        Func<IPAddress, bool> isAllowed,
        CancellationToken cancellation)
    {
        var addresses = IPAddress.TryParse(endpoint.Host, out var literal)
            ? [literal]
            : await resolve(endpoint.Host, cancellation).ConfigureAwait(false);

        var allowed = Array.FindAll(addresses, a => isAllowed(a));
        if (allowed.Length == 0) throw new HttpRequestException("adresse non publique refusée");

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(allowed, endpoint.Port, cancellation).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    // Without a proxy so that the check above is the last word on where the connection goes (a proxy would resolve the name itself).
    private static SocketsHttpHandler CreateHandler() => new()
    {
        ConnectTimeout = ConnectTimeout,
        AutomaticDecompression = DecompressionMethods.All,
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        ConnectCallback = (context, cancellation) => ConnectGuardedAsync(context.DnsEndPoint, Dns.GetHostAddressesAsync, IsPublicAddress, cancellation),
    };

    private static bool IsRedirect(HttpStatusCode status) => (int)status is >= 300 and < 400;

    private static Uri? RefererPage(string? referer) =>
        Uri.TryCreate(referer?.Trim(), UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && ExternalLinks.IsC411Host(uri.IdnHost) && uri.UserInfo.Length == 0
            ? uri
            : null;

    // What a browser sends by default: the whole address to the same site, only the origin to another one.
    private static string? RefererFor(Uri? page, Uri target)
    {
        if (page is null) return null;

        return page.IdnHost.Equals(target.IdnHost, StringComparison.OrdinalIgnoreCase)
            ? page.GetComponents(UriComponents.HttpRequestUrl, UriFormat.UriEscaped)
            : page.GetLeftPart(UriPartial.Authority) + "/";
    }

    private static async Task<string?> CookiesOrNullAsync(Func<Uri, Task<string?>> cookies, Uri target)
    {
        try
        {
            return await cookies(target).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn("Cookies of the C411 tab not available for a download: " + ex.Message);
            return null;
        }
    }

    /// <summary>The body, or null when it is bigger than <see cref="MaxBodyBytes"/>. Each wait for data has its own timeout.</summary>
    private async Task<byte[]?> ReadBodyAsync(HttpResponseMessage response, CancellationTokenSource timeout)
    {
        if (response.Content.Headers.ContentLength > MaxBodyBytes) return null;

        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            while (true)
            {
                timeout.CancelAfter(ReadTimeout);
                var read = await stream.ReadAsync(chunk, timeout.Token).ConfigureAwait(false);
                if (read == 0) break;
                if (buffer.Length + read > MaxBodyBytes) return null;
                buffer.Write(chunk, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }

        return buffer.ToArray();
    }
}
