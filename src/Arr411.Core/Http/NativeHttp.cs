using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Text.Json.Nodes;

namespace Arr411.Core.Http;

/// <summary>
/// Port of the Android app's native HTTP (Engine.request): one request described as JSON
/// {url, method, headers, body: {type: none|text|multipart}}, run outside the browser so there is no CORS
/// and the cookies (qBittorrent's SID) are kept between calls.
/// </summary>
public sealed class NativeHttp : IDisposable
{
    public const int MaxResponseBytes = 128 * 1024 * 1024;

    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan DefaultReadTimeout = TimeSpan.FromSeconds(60);

    private static readonly HashSet<string> Methods = ["GET", "POST", "PUT", "DELETE", "PATCH", "HEAD", "OPTIONS"];

    // Managed by the HTTP stack itself.
    private static readonly HashSet<string> Restricted = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Connection", "Content-Length", "Transfer-Encoding", "Expect", "Upgrade", "Keep-Alive", "TE", "Trailer",
    };

    private static readonly HashSet<string> ContentHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Content-Type", "Content-Language", "Content-Encoding", "Content-Disposition", "Content-Location", "Content-MD5",
        "Content-Range", "Allow", "Expires", "Last-Modified",
    };

    private readonly HttpClient _client;
    private readonly string _userAgent;
    private readonly TimeSpan _readTimeout;

    /// <param name="userAgent">Sent unless the request sets its own (a missing User-Agent is refused by some proxies).</param>
    /// <param name="handler">Replaces the real network stack (tests). Cookies, redirects and compression are then its business.</param>
    /// <param name="readTimeout">How long a response may stay silent (headers, then each chunk of the body).</param>
    public NativeHttp(string userAgent, HttpMessageHandler? handler = null, TimeSpan? readTimeout = null)
    {
        _userAgent = userAgent;
        _readTimeout = readTimeout ?? DefaultReadTimeout;
        _client = new HttpClient(handler ?? CreateHandler()) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public void Dispose() => _client.Dispose();

    /// <summary>Runs the request. Never throws for network problems: they come back as <see cref="HttpResult.Error"/>.</summary>
    public async Task<HttpResult> SendAsync(string requestJson, CancellationToken cancellation = default)
    {
        try
        {
            using var request = BuildRequest(requestJson);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(_readTimeout);

            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            var text = await ReadBodyAsync(response, timeout, _readTimeout).ConfigureAwait(false);
            return HttpResult.Ok((int)response.StatusCode, text);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return HttpResult.Fail("Réseau : requête annulée");
        }
        catch (OperationCanceledException)
        {
            return HttpResult.Fail("Réseau : délai dépassé");
        }
        catch (Exception ex)
        {
            return HttpResult.Fail(Describe(ex));
        }
    }

    // The redirects are not left to the built-in handler: it sends the headers (the API key) and the body (the password of a login, with a 307)
    // wherever the server points to.
    private static SameServerRedirectHandler CreateHandler() => new(new SocketsHttpHandler
    {
        ConnectTimeout = ConnectTimeout,
        AutomaticDecompression = DecompressionMethods.All,
        UseCookies = true,
        CookieContainer = new CookieContainer(),
        AllowAutoRedirect = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    });

    private HttpRequestMessage BuildRequest(string requestJson)
    {
        if (JsonNode.Parse(requestJson) is not JsonObject req) throw new FormatException("Invalid request");

        var urlText = Text(req["url"]) ?? "";
        if (!Uri.TryCreate(urlText, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new UriFormatException(urlText.Length == 0 ? "adresse vide" : urlText);

        var method = (Text(req["method"]) ?? "GET").ToUpperInvariant();
        if (!Methods.Contains(method)) throw new NotSupportedException("Invalid HTTP method: " + method);

        // qBittorrent's CSRF protection wants Origin/Referer matching its own host; the request may still override them.
        var origin = uri.Scheme + "://" + uri.Authority;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Origin"] = origin,
            ["Referer"] = origin + "/",
            ["User-Agent"] = _userAgent,
        };
        if (req["headers"] is JsonObject given)
        {
            foreach (var (name, value) in given)
            {
                if (value is not null) headers[name] = value.ToString();
            }
        }

        HttpContent? content = null;
        var body = req["body"] as JsonObject;
        switch (Text(body?["type"]) ?? "none")
        {
            case "text":
                content = new ByteArrayContent(Encoding.UTF8.GetBytes(Text(body!["text"]) ?? throw new FormatException("No value for text")));
                headers.TryAdd("Content-Type", "application/x-www-form-urlencoded");
                break;
            case "multipart":
                var (bytes, boundary) = Multipart.Build(body!["parts"] as JsonArray ?? throw new FormatException("No value for parts"));
                content = new ByteArrayContent(bytes);
                headers["Content-Type"] = "multipart/form-data; boundary=" + boundary;
                break;
        }

        var request = new HttpRequestMessage(new HttpMethod(method), uri) { Content = content };
        foreach (var (name, value) in headers)
        {
            if (Restricted.Contains(name)) continue;
            if (ContentHeaders.Contains(name)) content?.Headers.TryAddWithoutValidation(name, value);
            else request.Headers.TryAddWithoutValidation(name, value);
        }

        return request;
    }

    private static async Task<string> ReadBodyAsync(HttpResponseMessage response, CancellationTokenSource timeout, TimeSpan readTimeout)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            while (true)
            {
                timeout.CancelAfter(readTimeout);
                var read = await stream.ReadAsync(chunk, timeout.Token).ConfigureAwait(false);
                if (read == 0) break;
                buffer.Write(chunk, 0, read);
                if (buffer.Length > MaxResponseBytes) throw new InvalidDataException("réponse trop volumineuse");
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    private static string? Text(JsonNode? node) => JsonText.Str(node);

    /// <summary>The French message the web app shows for a network failure (same wording as the Android app).</summary>
    internal static string Describe(Exception ex)
    {
        var message = ex.Message;
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            switch (e)
            {
                case UriFormatException:
                    return "Adresse invalide : " + e.Message;
                case TimeoutException:
                case SocketException { SocketErrorCode: SocketError.TimedOut }:
                    return "Réseau : délai dépassé";
                case SocketException { SocketErrorCode: SocketError.HostNotFound or SocketError.TryAgain or SocketError.NoData }:
                    return $"Réseau : serveur introuvable ({message})";
                case SocketException { SocketErrorCode: SocketError.ConnectionRefused }:
                    return $"Réseau : connexion refusée ({message})";
                case AuthenticationException:
                    return $"Réseau : erreur SSL / certificat ({message})";
            }
        }

        if (ex is HttpRequestException http)
        {
            return http.HttpRequestError switch
            {
                HttpRequestError.NameResolutionError => $"Réseau : serveur introuvable ({message})",
                HttpRequestError.SecureConnectionError => $"Réseau : erreur SSL / certificat ({message})",
                HttpRequestError.ConnectionError => $"Réseau : connexion impossible ({message})",
                _ => "Réseau : " + message,
            };
        }

        return "Réseau : " + message;
    }
}
