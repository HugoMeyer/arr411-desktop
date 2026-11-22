using System.Net;

namespace Arr411.Core.Update;

/// <summary>
/// The HTTP side of the updater: every address is checked against the GitHub hosts before it is asked for, and redirects are followed by hand
/// (a handful at most, each one checked the same way), never by the handler.
/// </summary>
internal sealed class UpdateHttp : IDisposable
{
    private readonly HttpClient _client;

    public UpdateHttp(HttpMessageHandler handler) =>
        _client = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>The handler of the app: no redirect, no cookie, no decompression (the bytes of a download are the bytes of the file), the system proxy.</summary>
    public static HttpMessageHandler CreateHandler() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.None,
        ConnectTimeout = TimeSpan.FromSeconds(15),
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
    };

    /// <summary>
    /// The answer to a GET, once the redirects are followed. The headers are the ones <paramref name="prepare"/> sets on every request; none of them is
    /// a credential, so no hop can leak one.
    /// </summary>
    /// <exception cref="UpdateFailure">An address, first or after a redirect, is not an https address of GitHub; or too many redirects.</exception>
    public async Task<HttpResponseMessage> GetAsync(Uri address, Action<HttpRequestMessage> prepare, CancellationToken cancellation)
    {
        var uri = address;
        for (var redirects = 0; ; redirects++)
        {
            if (!UpdateHosts.IsAllowed(uri)) throw new UpdateFailure(UpdateText.Refused, $"address refused: {UpdateHosts.HostOf(uri)}");

            var request = new HttpRequestMessage(HttpMethod.Get, uri);
            prepare(request);
            var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
            if (!UpdateHosts.IsRedirect(response.StatusCode)) return response;

            var location = response.Headers.Location;
            response.Dispose();
            if (location is null) throw new UpdateFailure(UpdateText.DownloadFailed, "redirect without a destination");
            if (redirects >= UpdateHosts.MaxRedirects) throw new UpdateFailure(UpdateText.TooManyRedirects);

            uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
        }
    }

    /// <summary>The body of an answer, refused when it is longer than <paramref name="maxBytes"/>.</summary>
    public static async Task<byte[]> ReadAsync(HttpResponseMessage response, int maxBytes, CancellationToken cancellation)
    {
        if (response.Content.Headers.ContentLength > maxBytes) throw new UpdateFailure(UpdateText.UnreadableAnswer, "answer too long");

        await using var stream = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
        using var body = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellation).ConfigureAwait(false)) > 0)
        {
            if (body.Length + read > maxBytes) throw new UpdateFailure(UpdateText.UnreadableAnswer, "answer too long");

            body.Write(chunk, 0, read);
        }

        return body.ToArray();
    }

    public void Dispose() => _client.Dispose();
}
