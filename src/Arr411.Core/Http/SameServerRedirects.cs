using System.Net;
using Arr411.Core.Logging;

namespace Arr411.Core.Http;

/// <summary>What a redirect may do to a request that carries a secret (the API key of an *arr, the password of qBittorrent).</summary>
internal static class RedirectRules
{
    /// <summary>
    /// Whether a request may go on from <paramref name="from"/> to <paramref name="to"/> with everything it holds: to the same machine and the
    /// same service (the port only changes when http becomes https, as with a proxy that turns 80 into 443), and never from an encrypted
    /// connection to a clear one.
    /// </summary>
    public static bool StaysOnTheServer(Uri from, Uri to)
    {
        if (to.Scheme is not ("http" or "https")) return false;
        if (from.Scheme == Uri.UriSchemeHttps && to.Scheme != Uri.UriSchemeHttps) return false;
        if (!string.Equals(from.Host, to.Host, StringComparison.OrdinalIgnoreCase)) return false;

        return from.Port == to.Port || (from.Scheme == Uri.UriSchemeHttp && to.Scheme == Uri.UriSchemeHttps);
    }
}

/// <summary>
/// Follows the redirects of a response like the built-in handler does (same methods, same limit), but only those that stay on the server
/// (<see cref="RedirectRules"/>): what the request holds, which a 307 or a 308 sends again with its body, never reaches another machine.
/// A redirect that does not stay is the answer, as when the built-in handler refuses to leave https.
/// </summary>
internal sealed class SameServerRedirectHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    public const int MaxRedirects = 10;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        for (var followed = 0; ; followed++)
        {
            var next = Destination(request.RequestUri!, response);
            if (next is null) return response;

            if (followed == MaxRedirects)
            {
                response.Dispose();
                throw new HttpRequestException(HttpRequestError.ConfigurationLimitExceeded, "trop de redirections");
            }

            // Like the built-in handler: an authorization is not sent again, and a form that was posted becomes a plain request.
            request.Headers.Authorization = null;
            request.RequestUri = next;
            if (BecomesGet(response.StatusCode, request.Method))
            {
                request.Method = HttpMethod.Get;
                request.Content = null;
            }

            response.Dispose();
            response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
    }

    private static Uri? Destination(Uri requested, HttpResponseMessage response)
    {
        if (response.StatusCode is not (HttpStatusCode.MultipleChoices or HttpStatusCode.Moved or HttpStatusCode.Found
            or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)) return null;

        var location = response.Headers.Location;
        if (location is null) return null;

        var next = location.IsAbsoluteUri ? location : new Uri(requested, location);
        if (RedirectRules.StaysOnTheServer(requested, next)) return next;

        Log.Warn("A redirect to another server was not followed");
        return null;
    }

    private static bool BecomesGet(HttpStatusCode status, HttpMethod method) => status switch
    {
        HttpStatusCode.MultipleChoices or HttpStatusCode.Moved or HttpStatusCode.Found => method == HttpMethod.Post,
        HttpStatusCode.SeeOther => method != HttpMethod.Get && method != HttpMethod.Head,
        _ => false,
    };
}
