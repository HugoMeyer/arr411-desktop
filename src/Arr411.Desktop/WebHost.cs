using System.Diagnostics;
using Arr411.Core.Bridge;
using Arr411.Core.Hosting;
using Arr411.Core.Logging;
using Arr411.Core.Shell;
using Arr411.Desktop.Web;

namespace Arr411.Desktop;

/// <summary>
/// The web side of the window: the three views (the web app, the C411 site, the hidden engine), the bridges that answer what their pages ask,
/// and what happens when the browser process behind them stops. The views are controls of the window, which lays them out and disposes them;
/// this class makes them, starts them (once the window has a handle) and ends the bridges.
/// </summary>
internal sealed class WebHost : IDisposable
{
    private readonly AppServices _services;
    private readonly IMainWindow _window;
    private readonly TabState _tabs;
    private readonly IEngineShell _engineShell;
    private readonly IPageShell _pageShell;
    private readonly Action<string> _openExternal;

    private EngineBridge? _engineBridge;
    private PageBridge? _pageBridge;
    private long _startedAt;

    /// <param name="spaShell">What the web app asks of the window.</param>
    /// <param name="engineShell">What the engine asks of the window.</param>
    /// <param name="pageShell">What the C411 page asks of the window.</param>
    /// <param name="openExternal">Receives the links the user tried to open outside of a page (UI thread).</param>
    public WebHost(AppServices services, IMainWindow window, TabState tabs, ISpaShell spaShell, IEngineShell engineShell, IPageShell pageShell, Action<string> openExternal)
    {
        _services = services;
        _window = window;
        _tabs = tabs;
        _engineShell = engineShell;
        _pageShell = pageShell;
        _openExternal = openExternal;

        Spa = new SpaView { Dock = DockStyle.Fill };
        C411 = new C411View { Dock = DockStyle.Fill, Visible = false };
        Engine = new EngineView();
        Bridge = new SpaBridge(services.Config, services.Http, spaShell, tabs, Spa.PostToPage, services.Version, services.Updates);

        Spa.BrowserLost += OnViewLost;
        C411.BrowserLost += OnViewLost;
        Engine.BrowserLost += OnViewLost;
    }

    public SpaView Spa { get; }

    public C411View C411 { get; }

    /// <summary>Always running, whatever the window does; nobody sees it.</summary>
    public EngineView Engine { get; }

    /// <summary>The native side of the web app.</summary>
    public SpaBridge Bridge { get; }

    /// <summary>
    /// Set when the browser process behind the views stopped (all of them died with it, and none can be revived in place): what the program
    /// does next. Null while the browser runs, and when the window was leaving anyway.
    /// </summary>
    public BrowserLoss? Loss { get; private set; }

    /// <summary>Raised once, on the UI thread, when <see cref="Loss"/> is set. It is raised from inside the event of a view: the views are about to be closed.</summary>
    public event Action<BrowserLoss>? BrowserLost;

    /// <summary>Starts the browser and the views. Called once the window has a handle (and from the UI thread).</summary>
    public async void Start()
    {
        try
        {
            await StartAsync();
        }
        catch (Exception ex)
        {
            Log.Error("The web view could not start", ex);
            if (_window.IsClosed) return;

            MessageBox.Show(_window.DialogOwner, "Arr411 n'a pas pu démarrer son affichage web.\r\n\r\n" + ex.Message, "Arr411", MessageBoxButtons.OK, MessageBoxIcon.Error);
            _window.Quit();
        }
    }

    /// <summary>Passes what the engine emits to the C411 page.</summary>
    public void Emit(string json) => _pageBridge?.Emit(json);

    // Each page is told whether it can be seen: the one of the tab on screen, while the window is on screen. The engine always runs.
    public void UpdatePresentation()
    {
        var onScreen = _window.OnScreen;
        var c411 = _tabs.Current == Tabs.C411;
        Spa.SetPresented(onScreen && !c411);
        C411.SetPresented(onScreen && c411);
    }

    public void Dispose()
    {
        Bridge.Dispose();
        _pageBridge?.Dispose();
        _engineBridge?.Dispose();
    }

    private async Task StartAsync()
    {
        _startedAt = Stopwatch.GetTimestamp();

        // What the views are told is kept until their browser is there: a window that starts out of sight starts them out of sight.
        UpdatePresentation();

        var environment = await WebEnvironment.CreateAsync(_services.Paths);
        if (_window.IsClosed) return;

        var assets = new AssetStore(name => EmbeddedAssets.Open("app/" + name));
        var router = new HostRouter(assets, _services.Covers, Bridge.BuildState);
        await Spa.InitializeAsync(environment, router, Bridge.OnMessage, _openExternal);
        if (_window.IsClosed) return;

        var engineBridge = new EngineBridge(_services.Config, _services.Http, _engineShell, Engine.PostToPage);
        _engineBridge = engineBridge;
        var pageBridge = new PageBridge(engineBridge, _pageShell, new WindowsUserPresence(() => _window.Handle), _services.PageFetcher, _services.Paths.Downloads, C411.PostToPage)
        {
            OffScreenGrace = PageGrace(),
            RateWindow = PageRateWindow(),
        };
        _pageBridge = pageBridge;
        var engineRouter = new EngineRouter(new AssetStore(name => EmbeddedAssets.Open("engine/" + name)), engineBridge.BuildState);
        await Engine.InitializeAsync(environment, engineRouter, engineBridge);
        if (_window.IsClosed) return;

        await C411.InitializeAsync(environment, pageBridge, _openExternal);
    }

    // The views share one browser process, so each of them reports its loss. None can be revived in place: the window leaves, and the program
    // starts again (see Program), unless the browser had only just started, which means that it would stop again at once.
    private void OnViewLost(object? sender, EventArgs e)
    {
        // A window that is leaving anyway has nothing to start again for.
        if (Loss is not null || _window.IsLeaving) return;

        var restart = BrowserRecovery.ShouldRestart(Stopwatch.GetElapsedTime(_startedAt), MinimumRun());
        var loss = new BrowserLoss(restart, !_window.Visible, _tabs.Current);
        Loss = loss;
        BrowserLost?.Invoke(loss);
    }

    // How long the web views must have run for the program to start again when their browser stops: the default of BrowserRecovery, except in
    // Debug builds, whose end-to-end tests cannot wait for it (ARR411_RESTART_GRACE, in seconds).
    private static TimeSpan? MinimumRun()
    {
#if DEBUG
        if (int.TryParse(Environment.GetEnvironmentVariable("ARR411_RESTART_GRACE"), out var seconds) && seconds >= 0) return TimeSpan.FromSeconds(seconds);
#endif
        return null;
    }

    // How long the C411 page is still served after its tab left the screen: the default of PageBridge, except in Debug builds, whose
    // end-to-end tests cannot wait for it (ARR411_PAGE_GRACE, in seconds).
    private static TimeSpan PageGrace()
    {
#if DEBUG
        if (int.TryParse(Environment.GetEnvironmentVariable("ARR411_PAGE_GRACE"), out var seconds) && seconds >= 0) return TimeSpan.FromSeconds(seconds);
#endif
        return PageBridge.DefaultOffScreenGrace;
    }

    // How long what the C411 page does is counted for its limits: the default of PageBridge, except in Debug builds, whose end-to-end tests
    // send in a second what a person sends in a minute (ARR411_PAGE_RATE_WINDOW, in milliseconds).
    private static TimeSpan PageRateWindow()
    {
#if DEBUG
        if (int.TryParse(Environment.GetEnvironmentVariable("ARR411_PAGE_RATE_WINDOW"), out var milliseconds) && milliseconds > 0) return TimeSpan.FromMilliseconds(milliseconds);
#endif
        return PageBridge.DefaultRateWindow;
    }
}
