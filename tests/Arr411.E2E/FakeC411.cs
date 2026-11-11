using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Arr411.E2E;

/// <summary>A request the stand-in for C411 received (the cookie is the Cookie header as the site saw it).</summary>
public sealed record SiteRequest(string Method, string Path, string? Cookie);

/// <summary>
/// A stand-in for c411.org: a local HTTPS site whose pages have what the extension looks for. The app is started with c411.org (and its
/// subdomains) mapped to it, so that the tests never reach the real site.
/// </summary>
public sealed class FakeC411 : IAsyncDisposable
{
    public const string TorrentName = "Blade.Runner.2049.2017.MULTi.1080p.BluRay.x264-GROUP";

    /// <summary>The cookie the login page sets, as the site would see it come back.</summary>
    public const string AuthCookie = "auth=token-123";

    /// <summary>The background of every page: nothing else in the app has this colour, so it can be looked for in a picture of the window.</summary>
    public static readonly System.Drawing.Color PageColor = System.Drawing.Color.FromArgb(0xB5, 0xE6, 0x1D);

    private static readonly (byte[] File, string Hash) Sample = MakeTorrent();

    private readonly WebApplication _app;
    private readonly ConcurrentQueue<SiteRequest> _requests;
    private readonly X509Certificate2 _certificate;

    private FakeC411(WebApplication app, ConcurrentQueue<SiteRequest> requests, X509Certificate2 certificate, int port)
    {
        _app = app;
        _requests = requests;
        _certificate = certificate;
        Port = port;
    }

    /// <summary>A valid, minimal .torrent file: what the download button of the torrent page gives.</summary>
    public static byte[] Torrent => Sample.File;

    /// <summary>The hash of the info dictionary of <see cref="Torrent"/>: the one of the torrent page.</summary>
    public static string Hash => Sample.Hash;

    public int Port { get; }

    public IReadOnlyList<SiteRequest> Requests => _requests.ToArray();

    /// <param name="port">The port to listen on; any free one by default. A port that was just free lets a site come up where the app already looked for one.</param>
    public static async Task<FakeC411> StartAsync(int port = 0)
    {
        var requests = new ConcurrentQueue<SiteRequest>();
        var certificate = CreateCertificate();
        var app = Build(requests, certificate, port);
        try
        {
            await app.StartAsync();
            var address = app.Urls.FirstOrDefault() ?? throw new InvalidOperationException("The stand-in for C411 has no address.");
            return new FakeC411(app, requests, certificate, new Uri(address).Port);
        }
        catch
        {
            await app.DisposeAsync();
            certificate.Dispose();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        _certificate.Dispose();
    }

    private static (byte[] File, string Hash) MakeTorrent()
    {
        // The pieces are bytes outside ASCII, as in a real file: the extension must carry them through unchanged.
        var pieces = Enumerable.Range(0, 20).Select(i => (byte)(0xA0 + i)).ToArray();
        var info = Concat("d6:lengthi5e4:name3:abc12:piece lengthi16384e6:pieces20:"u8.ToArray(), pieces, "e"u8.ToArray());
        var file = Concat("d8:announce3:foo4:info"u8.ToArray(), info, "e"u8.ToArray());
        return (file, Convert.ToHexStringLower(SHA1.HashData(info)));
    }

    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(part => part).ToArray();

    private static X509Certificate2 CreateCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=c411.org", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("c411.org");
        names.AddDnsName("*.c411.org");
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));
        using var selfSigned = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));

        // Loaded again, so that the key is one the TLS stack of Windows can use.
        return X509CertificateLoader.LoadPkcs12(selfSigned.Export(X509ContentType.Pfx), null);
    }

    private static WebApplication Build(ConcurrentQueue<SiteRequest> requests, X509Certificate2 certificate, int port)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, port, listen => listen.UseHttps(certificate)));
        builder.Logging.ClearProviders();
        var app = builder.Build();

        app.Use(async (context, next) =>
        {
            var request = context.Request;
            requests.Enqueue(new SiteRequest(
                request.Method,
                request.Path + request.QueryString,
                request.Headers.Cookie.Count > 0 ? request.Headers.Cookie.ToString() : null));
            await next();
        });

        app.MapGet("/", () => Html("C411", Home()));
        app.MapGet("/frame", () => Html("Frame", """<p id="frame">frame</p>"""));
        app.MapGet("/dropzone", () => Html("Zone de dépôt", DropZonePage));

        // The session cookie of a login: no expiry date, so that the browser forgets it when it closes.
        app.MapGet("/login", (HttpResponse response) =>
        {
            response.Headers.SetCookie = AuthCookie + "; Path=/; Secure; HttpOnly; SameSite=Lax";
            return Html("Connexion", "<p>Connecté</p>");
        });

        app.MapGet("/torrents/{hash:length(40)}", (string hash) => Html(TorrentName, TorrentPage(hash)));
        app.MapGet("/torrents/{hash:length(40)}/download", (HttpResponse response) =>
        {
            response.Headers.ContentDisposition = $"attachment; filename=\"{TorrentName}.torrent\"";
            return Results.Bytes(Torrent, "application/x-bittorrent");
        });

        app.MapGet("/files/{name}", (string name, HttpResponse response) =>
        {
            response.Headers.ContentDisposition = $"attachment; filename=\"{name}\"";
            return name switch
            {
                "sample.torrent" => Results.Bytes(Torrent, "application/x-bittorrent"),
                "setup.exe" => Results.Bytes("MZ not a program"u8.ToArray(), "application/octet-stream"),
                "readme.txt" => Results.Bytes("hello"u8.ToArray(), "text/plain"),
                _ => Results.NotFound(),
            };
        });

        // Any other address is a page too (a search, a favicon...).
        app.MapFallback(() => Html("C411", "<p>Une page</p>"));

        return app;
    }

    // A zone that takes a drop itself, as the upload form of a site does, and says what it got: it covers the whole page, so that wherever
    // the drop is made it is made on the zone. What is dropped there is for the page, not for the app.
    private const string DropZonePage =
        """
        <div id="zone" style="position: fixed; inset: 0; background: #fff">
          Déposez ici
          <p id="files"></p>
          <p id="text"></p>
        </div>
        <script>
        const zone = document.getElementById('zone');
        zone.addEventListener('dragover', event => event.preventDefault());
        zone.addEventListener('drop', event => {
          event.preventDefault();
          document.getElementById('files').textContent = [...event.dataTransfer.files].map(file => file.name).join(',');
          document.getElementById('text').textContent = event.dataTransfer.getData('text/plain');
        });
        </script>
        """;

    private static IResult Html(string title, string body) => Results.Content(
        $$"""
        <!doctype html>
        <html lang="fr">
        <head><meta charset="utf-8"><title>{{title}}</title>
        <style>html, body { margin: 0; background: #B5E61D; font: 16px sans-serif; }</style></head>
        <body>
        {{body}}
        </body>
        </html>
        """,
        "text/html; charset=utf-8");

    private static string Home() =>
        $$"""
        <h1>C411</h1>
        <p>
        <a id="torrent" href="/torrents/{{Hash}}">Une fiche</a>
        <a id="outside" href="https://elsewhere.invalid/outside">Un autre site</a>
        <a id="magnet" href="magnet:?xt=urn:btih:{{Hash}}&amp;dn=Test">Un magnet</a>
        <a id="mailto" href="mailto:someone@elsewhere.invalid">Un courriel</a>
        <a id="settings" href="ms-settings:network">Les réglages</a>
        <a id="insecure" href="http://c411.org/insecure">Sans HTTPS</a>
        <a id="blank-c411" href="/torrents/{{Hash}}" target="_blank">Une fiche, dans une nouvelle fenêtre</a>
        <a id="blank-outside" href="https://elsewhere.invalid/blank" target="_blank">Un autre site, dans une nouvelle fenêtre</a>
        </p>
        <iframe id="frame" src="/frame" width="300" height="100"></iframe>
        """;

    // The button is the one of C411 (a button that says "Télécharger"). Its handler is attached from the script, not from an attribute:
    // the extension copies the attributes of the button, and a copy must not download anything by itself.
    private static string TorrentPage(string hash) =>
        $$"""
        <h1>{{TorrentName}}</h1>
        <dl><dt>Hash :</dt><dd>{{hash}}</dd></dl>
        <button id="download" type="button">Télécharger</button>
        <p>
        <a id="exe" href="/files/setup.exe">Un programme</a>
        <a id="txt" href="/files/readme.txt">Un texte</a>
        </p>
        <script>
        document.getElementById('download').addEventListener('click', async () => {
          if (new URLSearchParams(location.search).get('mode') === 'blob') {
            const response = await fetch('/files/sample.torrent');
            const blob = new Blob([await response.arrayBuffer()], { type: 'application/x-bittorrent' });
            const link = document.createElement('a');
            link.href = URL.createObjectURL(blob);
            link.download = '{{TorrentName}}.torrent';
            document.body.append(link);
            link.click();
            link.remove();
          } else {
            location.href = '/torrents/{{hash}}/download';
          }
        });
        </script>
        """;
}
