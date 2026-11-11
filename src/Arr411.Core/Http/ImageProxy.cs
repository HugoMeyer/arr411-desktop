using System.Net;
using System.Security.Cryptography;
using System.Text;
using Arr411.Core.Config;
using Arr411.Core.Hosting;
using Arr411.Core.Logging;

namespace Arr411.Core.Http;

/// <summary>
/// Cover images of Radarr, Sonarr and Lidarr (the web app's /mc route): downloaded with the API key,
/// which never reaches the page, and cached on disk for a week. Port of the Android SpaClient.cover.
/// </summary>
public sealed class ImageProxy : IDisposable
{
    public static readonly TimeSpan CacheLifetime = TimeSpan.FromDays(7);

    private const int MaxImageBytes = 15 * 1024 * 1024;
    private const long PruneThreshold = 150L * 1024 * 1024;
    private const long PruneTarget = 100L * 1024 * 1024;
    private const int MaxRedirects = 3;
    private const int MaxParallelDownloads = 6;

    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(45);

    // An image is only ever shown in an <img>: whatever a server puts behind the URL, it must not run as a page.
    private static readonly IReadOnlyDictionary<string, string> ResponseHeaders = new Dictionary<string, string>
    {
        ["Cache-Control"] = "private, max-age=600",
        ["Content-Security-Policy"] = "default-src 'none'; style-src 'unsafe-inline'; sandbox",
    };

    private readonly IConfigReader _config;
    private readonly string _cacheDir;
    private readonly HttpClient _client;
    private readonly SemaphoreSlim _downloads = new(MaxParallelDownloads);

    /// <param name="handler">Replaces the real network stack (tests). Must not follow redirects by itself.</param>
    public ImageProxy(IConfigReader config, string cacheDir, HttpMessageHandler? handler = null)
    {
        _config = config;
        _cacheDir = cacheDir;
        _client = new HttpClient(handler ?? new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(15),
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = false,
        })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    public void Dispose()
    {
        _client.Dispose();
        _downloads.Dispose();
    }

    /// <param name="service">radarr, sonarr or lidarr.</param>
    /// <param name="target">Absolute image URL, which must lie under the service's configured address.</param>
    public async Task<HostResponse> GetAsync(string? service, string? target, CancellationToken cancellation = default)
    {
        if (target is null || service is not ("radarr" or "sonarr" or "lidarr")) return HostResponse.Empty(400, "Bad Request");

        var baseUrl = (_config.GetString(service + "Url") ?? "").Trim().TrimEnd('/');
        var key = (_config.GetString(service + "Key") ?? "").Trim();
        if (baseUrl.Length == 0 || key.Length == 0 || !target.StartsWith(baseUrl + "/", StringComparison.Ordinal))
            return HostResponse.Empty(403, "Forbidden");

        var file = Path.Combine(_cacheDir, CacheName(target));
        if (File.GetLastWriteTimeUtc(file) + CacheLifetime > DateTime.UtcNow)
        {
            var fresh = await TryReadAsync(file).ConfigureAwait(false);
            if (fresh is not null) return Image(fresh, target);
        }

        var data = await FetchAsync(target, key, cancellation).ConfigureAwait(false);
        if (data is not null)
        {
            Store(file, data);
            return Image(data, target);
        }

        // Offline or failing: an out-of-date copy is better than a hole.
        var stale = await TryReadAsync(file).ConfigureAwait(false);
        return stale is null ? HostResponse.Empty(404, "Not Found") : Image(stale, target);
    }

    /// <summary>Keeps the cache under about 150 MB (oldest files go first). Blocking.</summary>
    public void Prune()
    {
        try
        {
            Prune(PruneThreshold, PruneTarget);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("Cover cache not pruned: " + ex.Message);
        }
    }

    internal void Prune(long threshold, long target)
    {
        if (!Directory.Exists(_cacheDir)) return;

        var files = new List<FileInfo>();
        long total = 0;
        foreach (var file in new DirectoryInfo(_cacheDir).EnumerateFiles())
        {
            if (file.Name.Contains(".tmp", StringComparison.Ordinal))
            {
                TryDelete(file);
                continue;
            }

            files.Add(file);
            total += file.Length;
        }

        if (total < threshold) return;

        files.Sort((a, b) => a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc));
        foreach (var file in files)
        {
            if (total < target) break;
            total -= file.Length;
            TryDelete(file);
        }
    }

    private async Task<byte[]?> FetchAsync(string target, string key, CancellationToken cancellation)
    {
        if (!Uri.TryCreate(target, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return null;

        await _downloads.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(FetchTimeout);

            for (var hop = 0; hop <= MaxRedirects; hop++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.TryAddWithoutValidation("X-Api-Key", key);
                using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);

                if (response.StatusCode is HttpStatusCode.Moved or HttpStatusCode.Redirect or HttpStatusCode.RedirectMethod
                    or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
                {
                    var location = response.Headers.Location;
                    if (location is null) return null;
                    var next = location.IsAbsoluteUri ? location : new Uri(uri, location);

                    // The API key must never be sent to another machine or service, nor in the clear after it went out encrypted.
                    if (!RedirectRules.StaysOnTheServer(uri, next)) return null;
                    uri = next;
                    continue;
                }

                if (response.StatusCode != HttpStatusCode.OK) return null;
                var type = response.Content.Headers.ContentType?.MediaType;
                if (type is not null && !type.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) return null;
                return await ReadLimitedAsync(response.Content, timeout.Token).ConfigureAwait(false);
            }

            return null;
        }
        catch (Exception ex)
        {
            Log.Warn($"Cover download failed ({ex.GetType().Name})");
            return null;
        }
        finally
        {
            _downloads.Release();
        }
    }

    private static async Task<byte[]?> ReadLimitedAsync(HttpContent content, CancellationToken cancellation)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellation).ConfigureAwait(false)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > MaxImageBytes) return null;
        }

        return buffer.Length > 0 ? buffer.ToArray() : null;
    }

    private void Store(string file, byte[] data)
    {
        var temp = file + ".tmp" + Guid.NewGuid().ToString("N");
        try
        {
            Directory.CreateDirectory(_cacheDir);
            File.WriteAllBytes(temp, data);
            File.Move(temp, file, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(new FileInfo(temp));
        }
    }

    private static async Task<byte[]?> TryReadAsync(string file)
    {
        try
        {
            return await File.ReadAllBytesAsync(file).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void TryDelete(FileInfo file)
    {
        try
        {
            file.Delete();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Busy or already gone.
        }
    }

    private static HostResponse Image(byte[] data, string target) => new()
    {
        Status = 200,
        Reason = "OK",
        ContentType = ImageSniffer.Detect(data) ?? TypeFromUrl(target),
        Body = new MemoryStream(data, writable: false),
        Headers = ResponseHeaders,
    };

    private static string TypeFromUrl(string target) =>
        Uri.TryCreate(target, UriKind.Absolute, out var uri) ? MimeTypes.For(uri.AbsolutePath) : MimeTypes.Default;

    private static string CacheName(string target) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(target)));
}
