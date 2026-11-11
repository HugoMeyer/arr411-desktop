using System.Text;

namespace Arr411.Core.Hosting;

/// <summary>
/// The tiny web server behind https://engine.arr411.local: the page that runs the extension's background logic, and nothing else.
/// Nothing listens on a port: the window answers the requests of its own web view.
/// </summary>
public sealed class EngineRouter
{
    public const string HostName = "engine.arr411.local";

    public const string PagePath = "/engine.html";

    private readonly AssetStore _files;
    private readonly Func<string> _state;
    private readonly string _htmlPolicy;

    /// <param name="files">The engine files: engine.html, shim-engine.js and background.js.</param>
    /// <param name="state">Builds the JSON served at /native/state. Called on the caller's thread, before any await.</param>
    public EngineRouter(AssetStore files, Func<string> state)
    {
        _files = files;
        _state = state;
        _htmlPolicy = BuildPolicy(files);
    }

    /// <summary>The security policy of the engine page (exposed for the tests).</summary>
    internal string HtmlPolicy => _htmlPolicy;

    public Task<HostResponse> HandleAsync(string method, Uri uri, CancellationToken cancellation = default)
    {
        if (!LocalResponses.IsReadOnly(method, out var head)) return Task.FromResult(LocalResponses.MethodNotAllowed());

        return Task.FromResult(LocalResponses.Finish(Route(uri), head, _htmlPolicy));
    }

    private HostResponse Route(Uri uri)
    {
        if (!uri.Host.Equals(HostName, StringComparison.OrdinalIgnoreCase)) return LocalResponses.NotFound();

        // Only what the page needs: the engine cannot be used to read anything else.
        return Uri.UnescapeDataString(uri.AbsolutePath) switch
        {
            "/" or PagePath => _files.Get("engine.html"),
            "/shim-engine.js" => _files.Get("shim-engine.js"),
            "/background.js" => _files.Get("background.js"),
            "/native/state" => LocalResponses.Json(_state()),
            "/favicon.ico" => LocalResponses.NoContent(),
            _ => LocalResponses.NotFound(),
        };
    }

    // The page has one inline script (it tells the window the engine is ready): its hash is allowed, no other inline code is.
    private static string BuildPolicy(AssetStore files)
    {
        var page = files.Get("engine.html");
        using var body = page.Body;
        if (page.Status != 200) throw new InvalidOperationException("The engine page is not embedded.");

        using var reader = new StreamReader(body, Encoding.UTF8);
        var scripts = string.Join(' ', InlineScripts.Hashes(reader.ReadToEnd()));
        return "default-src 'none'; script-src 'self' " + scripts + "; connect-src 'self'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";
    }
}
