using Arr411.Core.Bridge;
using Arr411.Core.Hosting;
using Microsoft.Web.WebView2.Core;

namespace Arr411.Desktop.Web;

/// <summary>
/// The hidden view that runs the background logic of the extension (watching the downloads, notifying when they are done).
/// It sits behind the other views, as small as can be, but stays visible so that the browser never puts it to sleep.
/// Its page talks to the window through window.QbtEngine (Scripts\engine-adapter.js).
/// </summary>
internal sealed class EngineView : LocalWebView
{
    private EngineBridge? _bridge;

    public EngineView()
        : base(EngineRouter.HostName, "engine", "scripts/engine-adapter.js", "https://" + EngineRouter.HostName + EngineRouter.PagePath, acceptsDrops: false)
    {
        Size = new Size(1, 1);
        Focusable = false;
    }

    public Task InitializeAsync(CoreWebView2Environment environment, EngineRouter router, EngineBridge bridge)
    {
        _bridge = bridge;
        return StartAsync(environment, router.HandleAsync, bridge.OnMessage, openExternal: null);
    }

    // The requests it was working on fail at once, instead of when the page is back.
    protected override void OnPageLost() => _bridge?.PageLost();

    // Nobody sees this view, so there is nobody to ask: the requests fail at once, with a message that says what to do, instead of each one timing out.
    protected override void OnReloadStopped() => _bridge?.GiveUp();
}
