using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Arr411.E2E;

public sealed record RecordedRequest(string Method, string Path, string? ApiKey, string? Cookie, int Status);

/// <summary>A torrent file that was sent to qBittorrent, with the options that came with it ("" when the form had none).</summary>
public sealed record AddedTorrent(string FileName, byte[] Content, string Category, string Paused, string Stopped);

/// <summary>Stand-ins for qBittorrent and Radarr, on a local port: what the app talks to in the tests.</summary>
public sealed class FakeServices : IAsyncDisposable
{
    public const string ApiKey = "radarr-test-key";
    public const string QbUser = "admin";
    public const string QbPassword = "p4ssw0rd-E2E";
    public const string SessionCookie = "session-1";

    /// <summary>The colour of every poster: nothing else in the app has it, so it can be looked for in a picture of the window.</summary>
    public static readonly System.Drawing.Color PosterColor = System.Drawing.Color.FromArgb(0xC8, 0x32, 0x9B);

    private static readonly byte[] Poster = MakePoster();

    private readonly WebApplication _app;
    private readonly ConcurrentQueue<RecordedRequest> _requests;
    private readonly QbState _qb;

    private FakeServices(WebApplication app, ConcurrentQueue<RecordedRequest> requests, QbState qb, string baseUrl)
    {
        _app = app;
        _requests = requests;
        _qb = qb;
        BaseUrl = baseUrl;
    }

    /// <summary>http://127.0.0.1:port, without a trailing slash.</summary>
    public string BaseUrl { get; }

    public IReadOnlyList<RecordedRequest> Requests => _requests.ToArray();

    /// <summary>What qBittorrent lists as its torrents: a JSON array, empty at first.</summary>
    public string Torrents
    {
        get => _qb.Torrents;
        set => _qb.Torrents = value;
    }

    /// <summary>The torrent files qBittorrent was given, in order.</summary>
    public IReadOnlyList<AddedTorrent> Added => _qb.Added.ToArray();

    /// <summary>The links (magnet links, addresses of .torrent files) qBittorrent was given, in order, one per entry.</summary>
    public IReadOnlyList<string> AddedLinks => _qb.Links.ToArray();

    /// <summary>
    /// The session ends just when the list of torrents is next asked for: that one request is refused, as qBittorrent does when the cookie has expired,
    /// whoever asks (the web app and the engine share their session).
    /// </summary>
    public void ExpireSessionOnNextTorrentList() => Interlocked.Exchange(ref _qb.RefuseNextList, 1);

    /// <param name="movies">How many films the stand-in for Radarr lists: Blade Runner 2049, then others.</param>
    public static async Task<FakeServices> StartAsync(int movies = 1)
    {
        var requests = new ConcurrentQueue<RecordedRequest>();
        var qb = new QbState();
        var app = Build(requests, qb, movies);
        await app.StartAsync();
        return new FakeServices(app, requests, qb, app.Urls.First().TrimEnd('/'));
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    private static byte[] MakePoster()
    {
        using var bitmap = new System.Drawing.Bitmap(100, 150);
        using (var graphics = System.Drawing.Graphics.FromImage(bitmap)) graphics.Clear(PosterColor);
        using var stream = new MemoryStream();
        bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
        return stream.ToArray();
    }

    // Shared with the request handlers, which run on other threads.
    private sealed class QbState
    {
        public volatile string Torrents = "[]";

        // 1 until the next request for the list of torrents has been refused.
        public int RefuseNextList;

        public ConcurrentQueue<AddedTorrent> Added { get; } = new();

        public ConcurrentQueue<string> Links { get; } = new();
    }

    private static string MovieJson(int id)
    {
        var title = id == 1 ? "Blade Runner 2049" : $"Film {id:000}";
        return $$"""{"id":{{id}},"title":"{{title}}","sortTitle":"{{title.ToLowerInvariant()}}","year":2017,"status":"released","monitored":true,"hasFile":true,"sizeOnDisk":123456789,"qualityProfileId":1,"tmdbId":{{335983 + id}},"runtime":164,"genres":["Science Fiction"],"images":[{"coverType":"poster","url":"/MediaCover/{{id}}/poster.jpg"}]}""";
    }

    private static WebApplication Build(ConcurrentQueue<RecordedRequest> requests, QbState qb, int movieCount)
    {
        var movies = "[" + string.Join(',', Enumerable.Range(1, movieCount).Select(MovieJson)) + "]";

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var app = builder.Build();

        app.Use(async (context, next) =>
        {
            await next();
            var request = context.Request;
            requests.Enqueue(new RecordedRequest(
                request.Method,
                request.Path + request.QueryString,
                request.Headers.TryGetValue("X-Api-Key", out var key) ? key.ToString() : null,
                request.Cookies["SID"],
                context.Response.StatusCode));
        });

        app.MapGet("/ping", () => "pong");
        app.MapGet("/ping/{**rest}", () => "pong");

        app.MapGet("/cookie/set", (string name, string value, HttpResponse response) =>
        {
            response.Cookies.Append(name, value, new CookieOptions { Path = "/" });
            return Results.Text("set");
        });

        app.MapMethods("/echo", ["GET", "POST", "PUT", "DELETE"], async (HttpRequest request) =>
        {
            using var reader = new StreamReader(request.Body, Encoding.UTF8);
            return Results.Json(new
            {
                method = request.Method,
                query = request.QueryString.Value,
                contentType = request.ContentType,
                apiKey = request.Headers["X-Api-Key"].ToString(),
                origin = request.Headers.Origin.ToString(),
                referer = request.Headers.Referer.ToString(),
                userAgent = request.Headers.UserAgent.ToString(),
                cookie = request.Headers.Cookie.ToString(),
                body = await reader.ReadToEndAsync(),
            });
        });

        // What a multipart form held, as the server saw it.
        app.MapPost("/upload", async (HttpRequest request) =>
        {
            var form = await request.ReadFormAsync();
            var fields = form.ToDictionary(field => field.Key, field => field.Value.ToString());
            var files = new List<object>();
            foreach (var file in form.Files)
            {
                using var stream = file.OpenReadStream();
                using var content = new MemoryStream();
                await stream.CopyToAsync(content);
                files.Add(new { name = file.Name, fileName = file.FileName, contentType = file.ContentType, length = content.Length, text = Encoding.UTF8.GetString(content.ToArray()) });
            }

            return Results.Json(new { fields, files });
        });

        app.MapGet("/status/{code:int}", (int code) => Results.Text("status " + code, "text/plain", Encoding.UTF8, code));

        app.MapGet("/big", async (int mb, HttpContext context) =>
        {
            context.Response.ContentType = "text/plain";
            var chunk = new byte[64 * 1024];
            Array.Fill(chunk, (byte)'a');
            for (long sent = 0; sent < mb * 1024L * 1024L; sent += chunk.Length)
            {
                await context.Response.Body.WriteAsync(chunk);
            }
        });

        // qBittorrent: a session cookie from the login is required by everything else.
        app.MapPost("/api/v2/auth/login", async (HttpRequest request, HttpResponse response) =>
        {
            var form = await request.ReadFormAsync();
            if (form["username"] != QbUser || form["password"] != QbPassword) return Results.Text("Fails.");
            response.Cookies.Append("SID", SessionCookie, new CookieOptions { Path = "/" });
            return Results.Text("Ok.");
        });
        app.MapGet("/api/v2/app/webapiVersion", (HttpRequest request) =>
            request.Cookies["SID"] == SessionCookie ? Results.Text("2.11.4") : Results.StatusCode(403));
        app.MapGet("/api/v2/torrents/info", (HttpRequest request) =>
            Interlocked.Exchange(ref qb.RefuseNextList, 0) == 1 || request.Cookies["SID"] != SessionCookie
                ? Results.StatusCode(403)
                : Results.Text(qb.Torrents, "application/json"));
        app.MapGet("/api/v2/sync/maindata", (HttpRequest request) =>
        {
            if (request.Cookies["SID"] != SessionCookie) return Results.StatusCode(403);

            // What the web app reads: the same torrents, as one full update keyed by hash.
            var torrents = new JsonObject();
            foreach (var item in JsonNode.Parse(qb.Torrents)!.AsArray())
            {
                var torrent = item!.AsObject();
                torrents[torrent["hash"]!.GetValue<string>()] = torrent.DeepClone();
            }

            return Results.Text(new JsonObject { ["rid"] = 1, ["full_update"] = true, ["torrents"] = torrents }.ToJsonString(), "application/json");
        });
        app.MapPost("/api/v2/torrents/add", async (HttpRequest request) =>
        {
            if (request.Cookies["SID"] != SessionCookie) return Results.StatusCode(403);

            var form = await request.ReadFormAsync();
            foreach (var link in form["urls"].ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) qb.Links.Enqueue(link);
            foreach (var file in form.Files.Where(file => file.Name == "torrents"))
            {
                using var content = new MemoryStream();
                await using (var stream = file.OpenReadStream()) await stream.CopyToAsync(content);
                qb.Added.Enqueue(new AddedTorrent(file.FileName, content.ToArray(), form["category"].ToString(), form["paused"].ToString(), form["stopped"].ToString()));
            }

            return Results.Text("Ok.");
        });
        app.MapGet("/api/v2/transfer/info", (HttpRequest request) =>
            request.Cookies["SID"] == SessionCookie ? Results.Text("""{"dl_info_speed":0,"up_info_speed":0}""", "application/json") : Results.StatusCode(403));
        app.MapGet("/api/v2/torrents/categories", (HttpRequest request) =>
            request.Cookies["SID"] == SessionCookie ? Results.Text("""{"films":{"name":"films"},"series":{"name":"series"}}""", "application/json") : Results.StatusCode(403));

        // Radarr: everything needs the API key.
        app.MapGet("/api/v3/movie", (HttpRequest request) =>
            request.Headers["X-Api-Key"] != ApiKey ? Results.StatusCode(401) : Results.Text(movies, "application/json"));
        app.MapGet("/api/v3/mediacover/{id:int}/{file}", (HttpRequest request) =>
            request.Headers["X-Api-Key"] != ApiKey ? Results.StatusCode(401) : Results.Bytes(Poster, "image/png"));
        app.MapGet("/api/v3/{**rest}", (HttpRequest request) =>
            request.Headers["X-Api-Key"] != ApiKey ? Results.StatusCode(401) : Results.Text("[]", "application/json"));

        return app;
    }
}
