using System.ComponentModel;
using System.Runtime.InteropServices;
using Arr411.Core.Bridge;
using Arr411.Core.Files;
using Arr411.Core.Logging;
using Arr411.Core.Shell;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Arr411.Desktop.Web;

/// <summary>
/// The C411 tab: the real website, with the scripts of the Android app injected in it (the buttons that send a torrent to qBittorrent).
/// Unlike the other views it reaches the Internet. What it may not do is leave c411.org: other sites open in the default browser,
/// popups and permission prompts are refused, and the page reaches the app only through <see cref="PageBridge"/>.
/// </summary>
internal sealed class C411View : Panel
{
    public const string HomeUrl = "https://c411.org/";

    private const int SessionCookieDays = 30;
    private const int CookiePassMilliseconds = 30_000;
    private const long ExternalOpenGapMilliseconds = 1_000;

    // What the right-click menu keeps besides the editing commands.
    private static readonly HashSet<string> MenuCommands = ["back", "forward", "reload", "copyLinkLocation", "copyImage", "copyImageLocation"];

    private readonly OfflinePanel _offline = new() { Dock = DockStyle.Fill, Visible = false };
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Theme.Background };
    private readonly LoadingBar _bar = new() { Dock = DockStyle.Top };
    private readonly System.Windows.Forms.Timer _cookieTimer = new() { Interval = CookiePassMilliseconds };
    private PageBridge? _bridge;
    private Action<string>? _openExternal;
    private string _userAgent = "";
    private string? _pendingUrl;
    private string? _lastUrl;
    private bool _ready;
    private bool _presented = true;
    private bool _wanted;
    private bool _navigated;
    private bool _failed;
    private bool _lost;
    private bool _downloadSeen;
    private long _lastExternal = -ExternalOpenGapMilliseconds;

    public C411View()
    {
        BackColor = Theme.Background;

        // What is dropped on the page reaches the window through Scripts\drop-adapter.js (the browser never hands a drop to anyone else).
        _web.AllowExternalDrop = true;
        _web.KeyDown += (_, e) => WebViewSettings.HandleKey(e, ShortcutHandler);

        // The first control is the one in front, and the one that docks last: the page fills what the bar leaves.
        Controls.Add(_offline);
        Controls.Add(_web);
        Controls.Add(_bar);
        _offline.RetryRequested += (_, _) => Reload();
        _cookieTimer.Tick += async (_, _) => await KeepSessionCookiesAsync();
    }

    /// <summary>
    /// Receives, on the UI thread, the shortcut of the app that the user pressed while the page had the focus; returns true when it took it
    /// (the page then never sees the key).
    /// </summary>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Func<Shortcut, bool>? ShortcutHandler { get; set; }

    /// <summary>
    /// Raised, on the UI thread, when the browser process behind the view stops. The view is closed for good, and so is every other view that
    /// shares the browser: none of them can be revived in place.
    /// </summary>
    public event EventHandler? BrowserLost;

    /// <summary>The user agent of the page (the native downloads for it use the same). UI thread.</summary>
    public string UserAgent => _userAgent;

    public bool CanGoBack => _web.CoreWebView2?.CanGoBack == true;

    /// <summary>The address of the page on screen, when it is a page of c411.org (an http one becomes https); nothing else is ever returned. UI thread.</summary>
    public string? CurrentUrl => _web.CoreWebView2 is { } core && ExternalLinks.C411Page(core.Source) is { } page ? page.AbsoluteUri : null;

    /// <summary>Gives the keyboard to the page (after a tab change), so that the user can scroll and type without a click.</summary>
    public void FocusContent()
    {
        if (_ready) _web.Focus();
    }

    /// <summary>
    /// Tells the page whether anyone can see it: a page nobody sees stops drawing and the browser may put it to sleep (the site is a heavy one),
    /// and what it asks of the engine is no longer served once it has been out of sight for a while (see <see cref="PageBridge.SetOnScreen"/>).
    /// The window says it at each change of what is on screen (see <see cref="WebViewVisibility"/>).
    /// </summary>
    public void SetPresented(bool presented)
    {
        if (presented == _presented) return;

        _presented = presented;
        WebViewVisibility.Apply(_web, presented);
        _bridge?.SetOnScreen(presented);
    }

    /// <param name="bridge">Answers the page.</param>
    /// <param name="openExternal">Receives the links the user opened outside of the site (UI thread).</param>
    public async Task InitializeAsync(CoreWebView2Environment environment, PageBridge bridge, Action<string> openExternal)
    {
        _bridge = bridge;
        _openExternal = openExternal;
        bridge.SetOnScreen(_presented);

        await _web.EnsureCoreWebView2Async(environment);
        WebViewVisibility.Apply(_web, _presented);
        var core = _web.CoreWebView2;
        WebViewSettings.Harden(core.Settings);
        core.Settings.IsStatusBarEnabled = true; // shows where a link goes
        _userAgent = core.Settings.UserAgent;

        core.NavigationStarting += OnNavigationStarting;
        core.NavigationCompleted += OnNavigationCompleted;
        core.ContentLoading += OnContentLoading;
        core.NewWindowRequested += OnNewWindowRequested;
        core.LaunchingExternalUriScheme += OnLaunchingExternalUriScheme;
        core.DownloadStarting += OnDownloadStarting;
        core.PermissionRequested += OnPermissionRequested;
        core.ContextMenuRequested += OnContextMenuRequested;
        core.WebMessageReceived += OnWebMessageReceived;
        core.ProcessFailed += OnProcessFailed;

        // The bridge first, then the scripts of the Android app that need it (the bundle does nothing without it, so frames are left alone).
        await core.AddScriptToExecuteOnDocumentCreatedAsync(EmbeddedAssets.ReadText("scripts/page-adapter.js"));
        await core.AddScriptToExecuteOnDocumentCreatedAsync(EmbeddedAssets.ReadText("scripts/drop-adapter.js"));
        var bundle = PageBundle.Compose(
            EmbeddedAssets.ReadText("page/shim-page.js"),
            EmbeddedAssets.ReadText("page/page-hook.js"),
            EmbeddedAssets.ReadText("page/content.js"));
        await core.AddScriptToExecuteOnDocumentCreatedAsync(bundle);

        _ready = true;
        if (_wanted && !_navigated) LoadPending();
    }

    /// <summary>
    /// Loads the home page if nothing was loaded yet. The tab is lazy: nothing reaches C411 until the user wants it, so the
    /// app does not need the VPN to start.
    /// </summary>
    public void EnsureLoaded()
    {
        _wanted = true;
        if (_ready && !_navigated) LoadPending();
    }

    /// <summary>Loads a page of c411.org (an http link becomes https). Anything else is refused.</summary>
    public bool Navigate(string url)
    {
        if (ExternalLinks.C411Page(url) is not { } page)
        {
            Log.Warn("A link was refused by the C411 tab");
            return false;
        }

        _pendingUrl = page.AbsoluteUri;
        _wanted = true;
        if (_ready) LoadPending();
        return true;
    }

    public void GoHome() => Navigate(HomeUrl);

    public bool GoBack()
    {
        if (_web.CoreWebView2 is not { CanGoBack: true } core) return false;

        core.GoBack();
        return true;
    }

    /// <summary>Loads the page again, or tries again after a failure.</summary>
    public void Reload()
    {
        if (!_ready) return;

        if (!_navigated)
        {
            EnsureLoaded();
            return;
        }

        if (_failed) _web.CoreWebView2.Navigate(_lastUrl ?? HomeUrl);
        else _web.CoreWebView2.Reload();
    }

    /// <summary>Sends a JSON message to the page. May be called from any thread.</summary>
    public void PostToPage(string json)
    {
        // The container of a view that was never shown has no window handle yet: the window (its parent) does the marshalling.
        if (IsDisposed) return;
        if (!InvokeRequired)
        {
            Post(json);
            return;
        }

        try
        {
            BeginInvoke(() => Post(json));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            // The window is closing.
        }
    }

    /// <summary>The Cookie header the page would send to this address, or null. May be called from any thread.</summary>
    public Task<string?> CookieHeaderAsync(Uri url) => this.InvokeAsync<string?>(async _ => await BuildCookieHeaderAsync(url));

    /// <summary>
    /// The browser forgets the cookies that have no expiry date when it closes, which would log the user out of C411 every time. This gives
    /// them one (30 days). Only the cookies of c411.org. Runs by itself while the tab is loaded; the window also calls it when it closes.
    /// </summary>
    public async Task KeepSessionCookiesAsync()
    {
        if (_lost || IsDisposed || _web.CoreWebView2 is not { } core) return;

        try
        {
            var manager = core.CookieManager;
            var expires = DateTime.UtcNow.AddDays(SessionCookieDays);
            foreach (var cookie in await manager.GetCookiesAsync(null))
            {
                if (!cookie.IsSession || !ExternalLinks.IsC411Host(cookie.Domain.TrimStart('.'))) continue;

                cookie.Expires = expires;
                manager.AddOrUpdateCookie(cookie);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or COMException or ObjectDisposedException)
        {
            Log.Warn("Cookies of the C411 tab not kept: " + ex.GetType().Name);
        }
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible && _ready) _web.Focus();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cookieTimer.Dispose();
            _web.Dispose();
        }

        base.Dispose(disposing);
    }

    private void LoadPending()
    {
        var url = _pendingUrl ?? HomeUrl;
        _pendingUrl = null;
        _navigated = true;
        _web.CoreWebView2.Navigate(url);
    }

    private void Post(string json)
    {
        if (!_lost && !IsDisposed && _web.CoreWebView2 is { } core) core.PostWebMessageAsJson(json);
    }

    private async ValueTask<string?> BuildCookieHeaderAsync(Uri url)
    {
        if (_web.CoreWebView2 is not { } core) return null;

        var cookies = await core.CookieManager.GetCookiesAsync(url.AbsoluteUri);
        return cookies.Count == 0 ? null : string.Join("; ", cookies.Select(cookie => cookie.Name + "=" + cookie.Value));
    }

    // The log never gets the addresses: they may carry secrets.
    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        var url = e.Uri;
        if (url.StartsWith("about:", StringComparison.OrdinalIgnoreCase)) return;

        if (url.StartsWith("blob:", StringComparison.OrdinalIgnoreCase))
        {
            if (!IsOwnBlob(url)) Refuse(e);
            return;
        }

        switch (ExternalLinks.Classify(url))
        {
            case LinkKind.C411:
                if (ExternalLinks.C411Page(url) is not { } page) Refuse(e);
                else if (!url.StartsWith("https:", StringComparison.OrdinalIgnoreCase)) Upgrade(e, page);
                else Started(url);
                break;
            case LinkKind.Magnet or LinkKind.Web:
                e.Cancel = true;
                if (e.IsUserInitiated) LeaveSite(url);
                else Log.Warn("A navigation of the C411 tab to another site was refused");
                break;
            default:
                Refuse(e);
                break;
        }
    }

    private void Started(string url)
    {
        _downloadSeen = false;
        _lastUrl = url;
        _bar.Start();
    }

    private static void Refuse(CoreWebView2NavigationStartingEventArgs e)
    {
        e.Cancel = true;
        Log.Warn("A navigation of the C411 tab was refused");
    }

    // Plain http to c411.org: the first request would travel in the clear. Go to the https page instead.
    private void Upgrade(CoreWebView2NavigationStartingEventArgs e, Uri page)
    {
        e.Cancel = true;
        BeginInvoke(() =>
        {
            if (!IsDisposed && _web.CoreWebView2 is { } core) core.Navigate(page.AbsoluteUri);
        });
    }

    // blob: addresses are pages the site made itself, as long as they were made by c411.org.
    private static bool IsOwnBlob(string url) =>
        url.StartsWith("blob:https://", StringComparison.OrdinalIgnoreCase)
        && Uri.TryCreate(url["blob:".Length..], UriKind.Absolute, out var inner)
        && ExternalLinks.IsC411Host(inner.Host)
        && inner.UserInfo.Length == 0;

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        _bar.Stop();
        if (e.IsSuccess)
        {
            _failed = false;
            _offline.Visible = false;
            if (!_cookieTimer.Enabled) _cookieTimer.Start();

            // A login is often what the page just did: keep it now, not at the next tick.
            _ = KeepSessionCookiesAsync();
            return;
        }

        // Refused on purpose (see OnNavigationStarting), or turned into a download: the page on screen is still the right one.
        if (e.WebErrorStatus == CoreWebView2WebErrorStatus.OperationCanceled) return;
        if (_downloadSeen && e.WebErrorStatus == CoreWebView2WebErrorStatus.ConnectionAborted) return;

        Log.Warn("C411 page not loaded: " + e.WebErrorStatus);
        _failed = true;
        _offline.Visible = true;
    }

    private void OnContentLoading(object? sender, CoreWebView2ContentLoadingEventArgs e) => _bridge?.NewPage();

    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        if (!e.IsUserInitiated)
        {
            Log.Warn("A popup of the C411 tab was refused");
            return;
        }

        if (ExternalLinks.C411Page(e.Uri) is { } page) _web.CoreWebView2.Navigate(page.AbsoluteUri);
        else LeaveSite(e.Uri);
    }

    // Links to other applications (mailto:, ms-settings:...) never start them. A magnet link is the exception: it goes to the web app.
    private void OnLaunchingExternalUriScheme(object? sender, CoreWebView2LaunchingExternalUriSchemeEventArgs e)
    {
        e.Cancel = true;
        if (e.IsUserInitiated && ExternalLinks.Classify(e.Uri) == LinkKind.Magnet) LeaveSite(e.Uri);
        else Log.Warn("A launch of another application by the C411 tab was refused");
    }

    // A page that opens a link on every click would fill the browser with tabs: one at a time.
    private void LeaveSite(string url)
    {
        if (ExternalLinks.Classify(url) is not (LinkKind.Web or LinkKind.Magnet))
        {
            Log.Warn("A link of the C411 tab was refused");
            return;
        }

        var now = Environment.TickCount64;
        if (now - _lastExternal < ExternalOpenGapMilliseconds)
        {
            Log.Warn("A link of the C411 tab was ignored: another one was just opened");
            return;
        }

        _lastExternal = now;
        _openExternal?.Invoke(url);
    }

    private async void OnDownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs e)
    {
        _downloadSeen = true;
        _bar.Stop();

        var deferral = e.GetDeferral();
        string? path = null;
        try
        {
            var operation = e.DownloadOperation;
            if (_bridge is not null) path = await _bridge.PlanDownloadAsync(operation.Uri, Path.GetFileName(e.ResultFilePath));
            if (path is null)
            {
                e.Cancel = true;
                return;
            }

            e.ResultFilePath = path;
            e.Handled = true; // the app tells the user itself (toast)
            var saved = path;
            operation.StateChanged += (_, _) => OnDownloadStateChanged(operation, saved);
        }
        catch (Exception ex)
        {
            Log.Error("A download of the C411 tab failed to start", ex);
            e.Cancel = true;
            if (path is not null) DownloadPolicy.DiscardIfEmpty(path);
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void OnDownloadStateChanged(CoreWebView2DownloadOperation operation, string path)
    {
        switch (operation.State)
        {
            case CoreWebView2DownloadState.Completed:
                _bridge?.DownloadFinished(path, success: true);
                break;
            case CoreWebView2DownloadState.Interrupted:
                _bridge?.DownloadFinished(path, success: false);
                break;
        }
    }

    private static void OnPermissionRequested(object? sender, CoreWebView2PermissionRequestedEventArgs e)
    {
        e.State = CoreWebView2PermissionState.Deny;
        Log.Warn($"A permission request of the C411 tab was denied: {e.PermissionKind}");
    }

    private static void OnContextMenuRequested(object? sender, CoreWebView2ContextMenuRequestedEventArgs e) =>
        WebViewSettings.RestrictContextMenu(e, MenuCommands);

    // Only the page of c411.org itself may talk to the app: its frames (third-party) and anything else are ignored.
    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (Uri.TryCreate(e.Source, UriKind.Absolute, out var source) && source.Scheme == Uri.UriSchemeHttps && ExternalLinks.IsC411Host(source.Host))
        {
            _bridge?.OnMessage(e.WebMessageAsJson);
        }
    }

    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        Log.Warn($"Web view process failed in the C411 tab: {e.ProcessFailedKind} ({e.Reason})");
        if (IsDisposed) return;

        if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.BrowserProcessExited)
        {
            // Nothing can be asked of the page any more.
            _lost = true;
            _ready = false;
            _cookieTimer.Stop();
            BrowserLost?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (e.ProcessFailedKind != CoreWebView2ProcessFailedKind.RenderProcessExited) return;

        BeginInvoke(Reload);
    }
}
