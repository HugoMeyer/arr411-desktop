using Arr411.Core.Hosting;
using Microsoft.Web.WebView2.Core;

namespace Arr411.Desktop.Web;

/// <summary>The view of the web app, at https://arr411.local. Its page talks to the window through window.Native (Scripts\spa-adapter.js).</summary>
internal sealed class SpaView : LocalWebView
{
    public SpaView()
        : base(HostRouter.HostName, "web app", "scripts/spa-adapter.js", "https://" + HostRouter.HostName + "/app/index.html", acceptsDrops: true)
    {
    }

    /// <param name="router">Answers the requests of the page.</param>
    /// <param name="onMessage">Receives the messages posted by the page (UI thread).</param>
    /// <param name="openExternal">Receives the links the page tried to open outside of itself (UI thread).</param>
    public Task InitializeAsync(CoreWebView2Environment environment, HostRouter router, Action<string> onMessage, Action<string> openExternal) =>
        StartAsync(environment, router.HandleAsync, onMessage, openExternal);
}
