using System.Web;
using Arr411.Core.Http;

namespace Arr411.Core.Hosting;

/// <summary>
/// The tiny web server behind https://arr411.local: the embedded web app, the cover proxy and the boot state.
/// Nothing listens on a port: the window answers the requests of its own web view.
/// </summary>
public sealed class HostRouter
{
    public const string HostName = "arr411.local";

    // The web app has no inline script and talks to nothing but itself (the native bridge goes through the host, not the network).
    private const string HtmlPolicy =
        "default-src 'none'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; "
        + "font-src 'self' data:; connect-src 'self'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";

    private const string AppPrefix = "/app/";

    private readonly AssetStore _app;
    private readonly ImageProxy _covers;
    private readonly Func<string> _state;

    /// <param name="app">The embedded web app (served under /app/).</param>
    /// <param name="covers">Serves /mc.</param>
    /// <param name="state">Builds the JSON served at /native/state. Called on the caller's thread, before any await.</param>
    public HostRouter(AssetStore app, ImageProxy covers, Func<string> state)
    {
        _app = app;
        _covers = covers;
        _state = state;
    }

    public async Task<HostResponse> HandleAsync(string method, Uri uri, CancellationToken cancellation = default)
    {
        if (!LocalResponses.IsReadOnly(method, out var head)) return LocalResponses.MethodNotAllowed();

        return LocalResponses.Finish(await RouteAsync(uri, cancellation).ConfigureAwait(false), head, HtmlPolicy);
    }

    private async Task<HostResponse> RouteAsync(Uri uri, CancellationToken cancellation)
    {
        if (!uri.Host.Equals(HostName, StringComparison.OrdinalIgnoreCase)) return LocalResponses.NotFound();

        var path = Uri.UnescapeDataString(uri.AbsolutePath);
        if (path.StartsWith(AppPrefix, StringComparison.Ordinal)) return _app.Get(path[AppPrefix.Length..]);

        switch (path)
        {
            case "/native/state":
                return LocalResponses.Json(_state());
            case "/favicon.ico":
                return LocalResponses.NoContent();
            case "/mc":
                var query = HttpUtility.ParseQueryString(uri.Query);
                return await _covers.GetAsync(query["s"], query["u"], cancellation).ConfigureAwait(false);
            default:
                return LocalResponses.NotFound();
        }
    }
}
