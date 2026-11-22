using System.Net;

namespace Arr411.Core.Http;

/// <summary>
/// Keeps the cookies of each server apart (scheme, host and port). One jar for everything would send qBittorrent's session cookie
/// to the Radarr or Sonarr of the same machine, because a cookie does not belong to a port. Only the servers that set a cookie take a place,
/// and the oldest one makes room when there are too many.
/// </summary>
internal sealed class OriginCookieHandler(HttpMessageHandler inner, int maxOrigins = OriginCookieHandler.DefaultMaxOrigins) : DelegatingHandler(inner)
{
    public const int DefaultMaxOrigins = 32;

    private readonly object _lock = new();
    private readonly Dictionary<string, CookieContainer> _jars = new(StringComparer.Ordinal);
    private readonly Queue<string> _oldestFirst = new();

    internal int OriginCount
    {
        get
        {
            lock (_lock) return _jars.Count;
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        var key = Key(uri);

        var cookies = Find(key)?.GetCookieHeader(uri);
        if (!string.IsNullOrEmpty(cookies))
        {
            // A header the request sets itself is kept, as the built-in handler does.
            var own = request.Headers.TryGetValues("Cookie", out var given) ? string.Join("; ", given) : null;
            request.Headers.Remove("Cookie");
            request.Headers.TryAddWithoutValidation("Cookie", own is { Length: > 0 } ? own + "; " + cookies : cookies);
        }

        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (response.Headers.TryGetValues("Set-Cookie", out var values))
        {
            var jar = FindOrAdd(key);
            foreach (var value in values)
            {
                try
                {
                    jar.SetCookies(uri, value);
                }
                catch (CookieException)
                {
                    // A malformed cookie is ignored.
                }
            }
        }

        return response;
    }

    internal static string Key(Uri uri) => $"{uri.Scheme}://{uri.Host.ToLowerInvariant()}:{uri.Port}";

    private CookieContainer? Find(string key)
    {
        lock (_lock) return _jars.GetValueOrDefault(key);
    }

    private CookieContainer FindOrAdd(string key)
    {
        lock (_lock)
        {
            if (_jars.TryGetValue(key, out var existing)) return existing;

            while (_jars.Count >= maxOrigins && _oldestFirst.TryDequeue(out var oldest)) _jars.Remove(oldest);

            var jar = new CookieContainer();
            _jars[key] = jar;
            _oldestFirst.Enqueue(key);
            return jar;
        }
    }
}
