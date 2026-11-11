using System.ComponentModel;
using Arr411.Core.Hosting;
using Arr411.Core.Logging;
using Arr411.Core.Shell;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Arr411.Desktop.Web;

/// <summary>
/// A web view that shows one page of the app and nothing else. Nothing comes from the network: the window answers the requests
/// for the address of the page itself, and the page talks to the window through an adapter script injected before it starts.
/// Everything that leaves the page is refused: requests to other sites, navigations, popups, downloads, permissions.
/// </summary>
internal abstract class LocalWebView : Panel
{
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Theme.Background };
    private readonly CancellationTokenSource _lifetime = new();
    private readonly string _host;
    private readonly string _subject;
    private readonly string _adapter;
    private readonly string _startUrl;
    private readonly bool _acceptsDrops;
    private Func<string, Uri, CancellationToken, Task<HostResponse>>? _handler;
    private Action<string>? _onMessage;
    private Action<string>? _openExternal;
    private bool _presented = true;
    private bool _lost;

    /// <param name="host">The only address the page may use.</param>
    /// <param name="subject">What the page is called in the log ("web app", "engine").</param>
    /// <param name="adapter">The embedded script that gives the page its native bridge.</param>
    /// <param name="startUrl">Where the page starts.</param>
    /// <param name="acceptsDrops">Whether the page takes the files and links dropped on it (it then reports them to the window, see Scripts\drop-adapter.js); the browser refuses them otherwise.</param>
    protected LocalWebView(string host, string subject, string adapter, string startUrl, bool acceptsDrops)
    {
        _host = host;
        _subject = subject;
        _adapter = adapter;
        _startUrl = startUrl;
        _acceptsDrops = acceptsDrops;

        // The browser keeps what is dropped from the window, unless the page is the one that reports it (a drop is only ever acted on by the window).
        _web.AllowExternalDrop = acceptsDrops;
        _web.KeyDown += (_, e) => WebViewSettings.HandleKey(e, ShortcutHandler);
        Controls.Add(_web);
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

    /// <summary>Gives the keyboard to the page (after a tab change), so that the user can scroll and type without a click.</summary>
    public void FocusContent()
    {
        if (_web.CoreWebView2 is not null) _web.Focus();
    }

    /// <summary>
    /// Tells the page whether anyone can see it: a page nobody sees stops drawing and the browser may put it to sleep. The window says it at
    /// each change of what is on screen (see <see cref="WebViewVisibility"/>). Never for the engine, which has to run whatever the window does.
    /// </summary>
    public void SetPresented(bool presented)
    {
        if (presented == _presented) return;

        _presented = presented;
        WebViewVisibility.Apply(_web, presented);
    }

    /// <summary>Whether the Tab key can reach the page. Off for a view that nobody sees.</summary>
    protected bool Focusable
    {
        get => _web.TabStop;
        set => _web.TabStop = value;
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

    public void Reload()
    {
        if (!_lost) _web.CoreWebView2?.Reload();
    }

    /// <param name="handler">Answers the requests of the page.</param>
    /// <param name="onMessage">Receives the messages posted by the page (UI thread).</param>
    /// <param name="openExternal">Receives the links the user tried to open outside of the page (UI thread); null refuses them.</param>
    protected async Task StartAsync(
        CoreWebView2Environment environment,
        Func<string, Uri, CancellationToken, Task<HostResponse>> handler,
        Action<string> onMessage,
        Action<string>? openExternal)
    {
        _handler = handler;
        _onMessage = onMessage;
        _openExternal = openExternal;

        await _web.EnsureCoreWebView2Async(environment);
        WebViewVisibility.Apply(_web, _presented);
        var core = _web.CoreWebView2;
        WebViewSettings.Harden(core.Settings);

        // Every request of the page goes through OnWebResourceRequested: the ones for the app are answered, the others never reach the network.
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += OnWebResourceRequested;
        core.WebMessageReceived += OnWebMessageReceived;
        core.NavigationStarting += OnNavigationStarting;
        core.NewWindowRequested += OnNewWindowRequested;
        core.DownloadStarting += OnDownloadStarting;
        core.PermissionRequested += OnPermissionRequested;
        core.ContextMenuRequested += OnContextMenuRequested;
        core.ProcessFailed += OnProcessFailed;

        await core.AddScriptToExecuteOnDocumentCreatedAsync(EmbeddedAssets.ReadText(_adapter));
        if (_acceptsDrops) await core.AddScriptToExecuteOnDocumentCreatedAsync(EmbeddedAssets.ReadText("scripts/drop-adapter.js"));
        core.Navigate(_startUrl);
    }

    /// <summary>The page died with its renderer; it is about to be loaded again. UI thread.</summary>
    protected virtual void OnPageLost()
    {
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _lifetime.Cancel();
            _web.Dispose();
        }

        base.Dispose(disposing);
    }

    private void Post(string json)
    {
        if (!_lost && !IsDisposed && _web.CoreWebView2 is { } core) core.PostWebMessageAsJson(json);
    }

    private bool IsOwnUri(string? uri) => Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && IsOwnUri(parsed);

    private bool IsOwnUri(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps && uri.Host.Equals(_host, StringComparison.OrdinalIgnoreCase);

    // Addresses that are not on the network: nothing to answer, nothing to block.
    private static bool IsInPage(Uri uri) => uri.Scheme is "data" or "blob" or "about";

    private async void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        var deferral = e.GetDeferral();
        try
        {
            if (!Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var uri) || !IsOwnUri(uri))
            {
                if (uri is null || !IsInPage(uri)) Refuse(e);
                return;
            }

            var method = e.Request.Method;
            HostResponse response;
            try
            {
                response = await _handler!(method, uri, _lifetime.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Error($"Request of the {_subject} failed", ex);
                response = HostResponse.Empty(500, "Internal Server Error");
            }

            e.Response = _web.CoreWebView2.Environment.CreateWebResourceResponse(response.Body, response.Status, response.Reason, HeaderBlock(response));
        }
        catch (Exception ex)
        {
            Log.Error($"Answer to the {_subject} failed", ex);
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void Refuse(CoreWebView2WebResourceRequestedEventArgs e)
    {
        e.Response = _web.CoreWebView2.Environment.CreateWebResourceResponse(null, 403, "Forbidden", "");
        Log.Warn($"A request of the {_subject} to another site was blocked");
    }

    private static string HeaderBlock(HostResponse response)
    {
        var lines = new List<string>();
        if (response.ContentType is not null) lines.Add("Content-Type: " + response.ContentType);
        foreach (var (name, value) in response.Headers) lines.Add(name + ": " + value);
        return string.Join("\r\n", lines);
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (IsOwnUri(e.Source)) _onMessage?.Invoke(e.WebMessageAsJson);
    }

    // The log never gets the addresses: they may carry secrets.
    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (IsOwnUri(e.Uri)) return;

        e.Cancel = true;
        if (e.IsUserInitiated && _openExternal is not null) _openExternal(e.Uri);
        else Log.Warn($"A navigation of the {_subject} was refused");
    }

    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        if (e.IsUserInitiated && _openExternal is not null) _openExternal(e.Uri);
        else Log.Warn($"A new window of the {_subject} was refused");
    }

    private void OnDownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs e)
    {
        e.Cancel = true;
        Log.Warn($"A download of the {_subject} was refused");
    }

    private void OnPermissionRequested(object? sender, CoreWebView2PermissionRequestedEventArgs e)
    {
        e.State = CoreWebView2PermissionState.Deny;
        Log.Warn($"A permission request of the {_subject} was denied: {e.PermissionKind}");
    }

    private static void OnContextMenuRequested(object? sender, CoreWebView2ContextMenuRequestedEventArgs e) => WebViewSettings.RestrictContextMenu(e);

    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        Log.Warn($"Web view process failed in the {_subject}: {e.ProcessFailedKind} ({e.Reason})");
        if (IsDisposed) return;

        if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.BrowserProcessExited)
        {
            _lost = true;
            BrowserLost?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (e.ProcessFailedKind != CoreWebView2ProcessFailedKind.RenderProcessExited) return;

        OnPageLost();
        BeginInvoke(Reload);
    }
}
