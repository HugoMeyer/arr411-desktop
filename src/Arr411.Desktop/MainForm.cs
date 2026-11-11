using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security;
using Arr411.Core.Bridge;
using Arr411.Core.Files;
using Arr411.Core.Hosting;
using Arr411.Core.Logging;
using Arr411.Core.Shell;
using Arr411.Desktop.Nav;
using Arr411.Desktop.Web;

namespace Arr411.Desktop;

/// <summary>
/// The window. A tab bar on the left; on the right the web app (every tab but C411), which answers its native calls here, or the C411 site
/// with the scripts that send a torrent to qBittorrent; behind them, the hidden engine that watches the downloads. It also takes what the
/// outside world hands over (magnet links, .torrent files, another launch of the program, drops) and lives on in the notification area.
/// </summary>
internal sealed class MainForm : Form, ISpaShell, IEngineShell, IPageShell, IAppMenuHost
{
    // Sizes in dp: 1 dp is 1 pixel at 100 % scale.
    private const int DefaultWidth = 1100;
    private const int DefaultHeight = 720;
    private const int MinimumWidth = 640;
    private const int MinimumHeight = 480;
    private const int ToastMaxWidth = 520;
    private const int ToastBottomMargin = 36;

    private const int CookieWaitMilliseconds = 3000;

    private const string TrayHint = "Arr411 reste actif dans la zone de notification pour suivre vos téléchargements. Clic droit sur l'icône pour le quitter.";

    private readonly AppServices _services;
    private readonly ShellIntegration _shell;
    private readonly TabState _tabs = new();
    private readonly SpaView _spa = new() { Dock = DockStyle.Fill };
    private readonly C411View _c411 = new() { Dock = DockStyle.Fill, Visible = false };
    private readonly EngineView _engine = new();
    private readonly NavBar _nav = new();
    private readonly SpaBridge _bridge;
    private readonly ContextMenuStrip _appMenu;
    private readonly ContextMenuStrip _trayMenu;
    private readonly ContextMenuStrip _c411Menu;
    private readonly TrayIcon _tray;

    private readonly Label _toast = new()
    {
        AutoSize = false,
        Visible = false,
        ForeColor = Theme.Text,
        BackColor = Theme.Raised,
        TextAlign = ContentAlignment.MiddleCenter,
    };

    private readonly System.Windows.Forms.Timer _toastTimer = new() { Interval = 2500 };

    // What reaches the window before it has a handle (the request this very launch comes with) waits here.
    private readonly object _earlyGate = new();
    private List<LaunchRequest>? _early = [];

    private EngineBridge? _engineBridge;
    private PageBridge? _pageBridge;
    private Font? _toastFont;
    private int _toastFontDpi;
    private bool _foreground;
    private bool _startHidden;
    private bool _webStarted;
    private long _webStartedAt;
    private bool _exiting;
    private bool _cookiesKept;
    private bool _maximized;
    private bool _dropAccepted;

    public MainForm(AppServices services, LaunchRequest request)
    {
        _services = services;
        _shell = new ShellIntegration(ShellIntegration.ProgramPath);
        _startHidden = request.Background;
        _foreground = !request.Background;

        AutoScaleMode = AutoScaleMode.None;
        Text = "Arr411";
        BackColor = Theme.Background;
        ForeColor = Theme.Text;
        Icon = LoadIcon(null);
        AllowDrop = true;

        _bridge = new SpaBridge(services.Config, services.Http, this, _tabs, _spa.PostToPage, services.Version);
        if (request.Background) _bridge.SetForeground(false);

        // The views that fill the window go in front: the control at index 0 docks last and takes what the bar leaves.
        // The engine goes last, so that it stays behind everything.
        Controls.Add(_spa);
        Controls.Add(_c411);
        Controls.Add(_toast);
        Controls.Add(_nav);
        Controls.Add(_engine);
        _toastTimer.Tick += (_, _) => HideToast();
        ApplyDpi();
        RestorePlacement();

        _nav.TabInvoked += OnTabInvoked;
        _nav.TabAlternate += OnTabAlternate;
        _nav.MenuRequested += ShowAppMenu;
        _spa.ShortcutHandler = OnShortcut;
        _c411.ShortcutHandler = OnShortcut;
        _spa.BrowserLost += OnBrowserLost;
        _c411.BrowserLost += OnBrowserLost;
        _engine.BrowserLost += OnBrowserLost;

        _appMenu = AppMenu.Build(this, tray: false);
        _trayMenu = AppMenu.Build(this, tray: true);
        _c411Menu = AppMenu.BuildC411(
            reload: () =>
            {
                _c411.Reload();
                ShowTab(Tabs.C411, tellSpa: true);
            },
            home: () =>
            {
                _c411.GoHome();
                ShowTab(Tabs.C411, tellSpa: true);
            },
            hasLink: () => _c411.CurrentUrl is not null,
            copyLink: () =>
            {
                if (_c411.CurrentUrl is { } link) ((ISpaShell)this).CopyText(link);
            },
            settings: () => _bridge.Share(SharePayload.Settings()));

        _tray = new TrayIcon(LoadIcon(SystemInformation.SmallIconSize) ?? SystemIcons.Application, _trayMenu);
        _tray.Opened += (_, _) => ShowWindow();
        _tray.NotificationClicked += (_, _) =>
        {
            ShowWindow();
            ShowTab(Tabs.Qbit, tellSpa: true);
        };

        // The web app opens on the tab decided here: it is asked for it while it boots.
        var start = StartTab.Resolve(services.Config, services.Ui.LastTab, request.Tab);
        ApplyTab(_tabs.Show(start, tellSpa: true), start);
    }

    /// <summary>
    /// Set when the window closed because the browser process behind its views stopped (all of them died with it, and none can be revived in
    /// place): what the program does next. Null when the window closed for any other reason.
    /// </summary>
    public BrowserLoss? Lost { get; private set; }

    /// <summary>Hands the window something to do: a launch of the program, from this process or another one. May be called from any thread.</summary>
    public void Post(LaunchRequest request)
    {
        lock (_earlyGate)
        {
            if (_early is not null)
            {
                _early.Add(request);
                return;
            }
        }

        try
        {
            BeginInvoke(() => RunRequest(request));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            // The window is closing: nobody is left to take it.
        }
    }

    void ISpaShell.SelectTab(string tab) => ShowTab(tab, tellSpa: false);

    void ISpaShell.OpenC411(string url)
    {
        // First the page, then the tab: the tab would otherwise load the home page for nothing.
        _c411.Navigate(url);
        ShowTab(Tabs.C411, tellSpa: false);
    }

    void ISpaShell.ReloadC411() => _c411.Reload();

    void ISpaShell.OpenExternal(string url) => OpenExternal(url);

    void ISpaShell.Toast(string text) => ShowToast(text);

    void ISpaShell.SetBadge(int count)
    {
        _nav.Badge = count;
        _tray.SetActive(count);
    }

    void ISpaShell.CopyText(string text)
    {
#if DEBUG
        // Debug builds only, for the end-to-end tests, which must not overwrite what the user has copied: a file instead of the clipboard.
        if (Environment.GetEnvironmentVariable("ARR411_CLIPBOARD_SINK") is { Length: > 0 } sink)
        {
            try
            {
                File.WriteAllText(sink, text);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn("Clipboard sink not written: " + ex.Message);
            }

            return;
        }
#endif
        try
        {
            Clipboard.SetDataObject(text, copy: true, retryTimes: 5, retryDelay: 100);
        }
        catch (ExternalException ex)
        {
            Log.Warn("Clipboard unavailable: " + ex.Message);
        }
    }

    void ISpaShell.Dropped(DroppedContent content) => OnDropped(content);

    void IEngineShell.Notify(string title, string text) => _tray.Notify(title, text);

    void IEngineShell.OpenExternal(string url) => OpenExternal(url);

    void IEngineShell.Emit(string json) => _pageBridge?.Emit(json);

    string IPageShell.UserAgent => _c411.UserAgent;

    void IPageShell.Toast(string text) => ShowToast(text);

    Task<string?> IPageShell.CookieHeaderAsync(Uri url) => _c411.CookieHeaderAsync(url);

    void IPageShell.Dropped(DroppedContent content) => OnDropped(content);

    void IAppMenuHost.OpenWindow() => ShowWindow();

    void IAppMenuHost.OpenSettings()
    {
        ShowWindow();
        _bridge.Share(SharePayload.Settings());
    }

    void IAppMenuHost.Associate()
    {
        try
        {
            _shell.Register();
        }
        catch (Exception ex) when (IsRegistryFailure(ex))
        {
            Log.Warn("Registration failed: " + ex.GetType().Name);
            Fail("Arr411 n'a pas pu s'enregistrer pour les liens magnet et les fichiers .torrent.");
            return;
        }

        // Windows decides by itself what opens a link: the user has to pick Arr411 in its settings.
        ShellIntegration.OpenDefaultAppsSettings();
        if (Visible) ShowToast("Arr411 est enregistré : choisissez-le dans les réglages de Windows.");
    }

    bool IAppMenuHost.StartWithWindows
    {
        get => _shell.StartsWithWindows;
        set
        {
            try
            {
                _shell.SetStartWithWindows(value);
            }
            catch (Exception ex) when (IsRegistryFailure(ex))
            {
                Log.Warn("Start with Windows failed: " + ex.GetType().Name);
                Fail("Impossible de modifier le démarrage avec Windows.");
            }
        }
    }

    bool IAppMenuHost.CloseToTray
    {
        get => _services.Ui.CloseToTray;
        set => _services.Ui.CloseToTray = value;
    }

    void IAppMenuHost.OpenLogs()
    {
        try
        {
            var folder = Path.GetDirectoryName(_services.Paths.LogFile) ?? _services.Paths.Root;
            Directory.CreateDirectory(folder);
            var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            var start = new ProcessStartInfo(explorer) { UseShellExecute = false };
            start.ArgumentList.Add(folder);
            using var process = Process.Start(start);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
        {
            Log.Warn("The logs folder could not be opened: " + ex.GetType().Name);
        }
    }

    void IAppMenuHost.ShowAbout()
    {
        var text = "Arr411 Desktop " + _services.Version + "\r\n\r\n"
            + "Radarr, Sonarr, Lidarr, qBittorrent et C411 dans une seule fenêtre.\r\n\r\n"
            + "Licence MIT — © 2026 JoK3RZz\r\n"
            + "https://github.com/HugoMeyer/arr411-desktop";
        MessageBox.Show(Visible ? this : null, text, "À propos d'Arr411", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    void IAppMenuHost.Quit() => Quit();

    protected override void SetVisibleCore(bool value)
    {
        // Started with --background (by Windows, at sign-in): the window stays out of sight until someone asks for it. It still gets its
        // handle, which is what starts the web views.
        if (value && _startHidden)
        {
            _startHidden = false;
            if (!IsHandleCreated) CreateHandle();
            value = false;
        }

        base.SetVisibleCore(value);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        NativeMethods.ColorTitleBar(Handle, Theme.Bar, Theme.Text, Theme.Accent);
        ApplyDpi();

        List<LaunchRequest>? early;
        lock (_earlyGate)
        {
            early = _early;
            _early = null;
        }

        if (early is not null)
        {
            foreach (var request in early) BeginInvoke(() => RunRequest(request));
        }

        if (_webStarted) return;

        _webStarted = true;
        BeginInvoke(StartWeb);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        // After the framework has scaled the window by itself.
        BeginInvoke(ApplyDpi);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (WindowState != FormWindowState.Minimized) _maximized = WindowState == FormWindowState.Maximized;
        PositionToast();
        UpdateForeground();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        UpdateForeground();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        if (e.Cancel) return;

        var reason = e.CloseReason;
        if (reason == CloseReason.UserClosing && !_exiting && _services.Ui.CloseToTray)
        {
            // The cross only hides the window: the downloads go on being watched, and the icon is the way back.
            e.Cancel = true;
            SaveWindowState();
            Hide();
            ShowTrayHintOnce();
            return;
        }

        SaveWindowState();
        if (reason is CloseReason.UserClosing or CloseReason.None && !_cookiesKept)
        {
            // The C411 session cookies reach the disk when the browser closes: they get their expiry date first, then the window goes.
            e.Cancel = true;
            _exiting = true;
            Hide();
            _ = CloseAfterCookiesAsync();
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _toastTimer.Dispose();
        _bridge.Dispose();
        _pageBridge?.Dispose();
        _engineBridge?.Dispose();
        _tray.Dispose();
        _trayMenu.Dispose();
        _appMenu.Dispose();
        _c411Menu.Dispose();
        _toastFont?.Dispose();
        base.OnFormClosed(e);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        // The keys pressed while a web view has the focus come through its KeyDown event instead (see WebViewSettings.HandleKey).
        var shortcut = Shortcuts.Resolve((int)(keyData & Keys.KeyCode), (keyData & Keys.Control) != 0, (keyData & Keys.Shift) != 0, (keyData & Keys.Alt) != 0);
        if (shortcut is { } found && OnShortcut(found)) return true;

        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnDragEnter(DragEventArgs drgevent)
    {
        base.OnDragEnter(drgevent);
        _dropAccepted = ParseDrop(drgevent.Data).HasWork;
        drgevent.Effect = _dropAccepted ? DragDropEffects.Copy : DragDropEffects.None;
    }

    protected override void OnDragOver(DragEventArgs drgevent)
    {
        base.OnDragOver(drgevent);
        drgevent.Effect = _dropAccepted ? DragDropEffects.Copy : DragDropEffects.None;
    }

    protected override void OnDragLeave(EventArgs e)
    {
        base.OnDragLeave(e);
        _dropAccepted = false;
    }

    protected override void OnDragDrop(DragEventArgs drgevent)
    {
        base.OnDragDrop(drgevent);
        _dropAccepted = false;
        var request = ParseDrop(drgevent.Data);
        if (request.HasWork) Post(request);
    }

    private static Icon? LoadIcon(Size? size)
    {
        using var stream = EmbeddedAssets.Open("app.ico");
        if (stream is null) return null;
        return size is { } wanted ? new Icon(stream, wanted) : new Icon(stream);
    }

    private static bool IsRegistryFailure(Exception ex) => ex is IOException or UnauthorizedAccessException or SecurityException;

    private int Px(int dp) => (int)Math.Round(dp * DeviceDpi / 96.0);

    // Everything the window sizes itself, from the DPI of the screen it is on. Safe to call again at any time.
    private void ApplyDpi()
    {
        MinimumSize = new Size(Px(MinimumWidth), Px(MinimumHeight));
        _toast.Padding = new Padding(Px(16), Px(10), Px(16), Px(10));

        if (_toastFont is null || _toastFontDpi != DeviceDpi)
        {
            var previous = _toastFont;
            _toastFontDpi = DeviceDpi;
            _toastFont = new Font("Segoe UI", 40f / 3f * DeviceDpi / 96f, FontStyle.Regular, GraphicsUnit.Pixel);
            _toast.Font = _toastFont;
            previous?.Dispose();
        }

        if (_toast.Visible) ShowToast(_toast.Text);
    }

    private void RestorePlacement()
    {
        var workAreas = Screen.AllScreens.Select(screen => screen.WorkingArea).ToList();
        if (_services.Ui.Window is { } saved && WindowPlacement.Fit(saved, workAreas, MinimumSize) is { } fit)
        {
            StartPosition = FormStartPosition.Manual;
            Bounds = new Rectangle(fit.X, fit.Y, fit.Width, fit.Height);
            if (fit.Maximized) WindowState = FormWindowState.Maximized;
            _maximized = fit.Maximized;
            return;
        }

        // Centred on the screen the pointer is on, which is where the usual size has to fit.
        StartPosition = FormStartPosition.CenterScreen;
        var wanted = new Size(Px(DefaultWidth), Px(DefaultHeight));
        ClientSize = wanted;
        ClientSize = WindowPlacement.FitClient(wanted, Size - ClientSize, Screen.FromPoint(MousePosition).WorkingArea);
    }

    private void SaveWindowState()
    {
        var bounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        if (bounds.Width < 1 || bounds.Height < 1) return;

        var maximized = WindowState == FormWindowState.Maximized || (WindowState == FormWindowState.Minimized && _maximized);
        _services.Ui.Window = new WindowBounds(bounds.X, bounds.Y, bounds.Width, bounds.Height, maximized);
    }

    private async void StartWeb()
    {
        try
        {
            await StartWebAsync();
        }
        catch (Exception ex)
        {
            Log.Error("The web view could not start", ex);
            if (IsDisposed) return;

            MessageBox.Show(Visible ? this : null, "Arr411 n'a pas pu démarrer son affichage web.\r\n\r\n" + ex.Message, "Arr411", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Quit();
        }
    }

    private async Task StartWebAsync()
    {
        _webStartedAt = Stopwatch.GetTimestamp();

        // What the views are told is kept until their browser is there: a window that starts out of sight starts them out of sight.
        UpdatePresentation();

        var environment = await WebEnvironment.CreateAsync(_services.Paths);
        if (IsDisposed) return;

        var assets = new AssetStore(name => EmbeddedAssets.Open("app/" + name));
        var router = new HostRouter(assets, _services.Covers, _bridge.BuildState);
        await _spa.InitializeAsync(environment, router, _bridge.OnMessage, OpenExternal);
        if (IsDisposed) return;

        var engineBridge = new EngineBridge(_services.Config, _services.Http, this, _engine.PostToPage);
        _engineBridge = engineBridge;
        var pageBridge = new PageBridge(engineBridge, this, _services.PageFetcher, _services.Paths.Downloads, _c411.PostToPage)
        {
            OffScreenGrace = PageGrace(),
            RateWindow = PageRateWindow(),
        };
        _pageBridge = pageBridge;
        var engineRouter = new EngineRouter(new AssetStore(name => EmbeddedAssets.Open("engine/" + name)), engineBridge.BuildState);
        await _engine.InitializeAsync(environment, engineRouter, engineBridge);
        if (IsDisposed) return;

        await _c411.InitializeAsync(environment, pageBridge, OpenExternal);
    }

    private void RunRequest(LaunchRequest request)
    {
        if (IsDisposed) return;

        try
        {
            if (request.Quit)
            {
                Quit();
                return;
            }

            if (request.Ignored > 0) Log.Warn("Some arguments of a launch were ignored: " + request.Ignored);
            if (!request.Background || request.HasWork) ShowWindow();
            if (request.C411Url is { } url)
            {
                _c411.Navigate(url);
                ShowTab(Tabs.C411, tellSpa: true);
            }

            if (request.Tab is { } tab) ShowTab(tab, tellSpa: true);
            if (request.Magnets.Count > 0) _bridge.Share(SharePayload.Text(string.Join('\n', request.Magnets)));
            if (request.TorrentFiles.Count > 0) _ = ShareFilesAsync(request.TorrentFiles);
        }
        catch (Exception ex)
        {
            Log.Error("A launch request failed", ex);
        }
    }

    private async Task ShareFilesAsync(IReadOnlyList<string> paths)
    {
        try
        {
            var files = new List<TorrentFile>();
            foreach (var path in paths)
            {
                try
                {
                    files.Add(await Task.Run(() => TorrentFile.Read(path)));
                }
                catch (TorrentFileException ex)
                {
                    if (!IsDisposed) ShowToast(ex.Message);
                }
            }

            if (files.Count > 0 && !IsDisposed) _bridge.Share(SharePayload.Files(files));
        }
        catch (Exception ex)
        {
            Log.Error("Sharing torrent files failed", ex);
        }
    }

    private LaunchRequest ParseDrop(IDataObject? data)
    {
        try
        {
            // The paths of files come from Explorer: all of them count, and none is taken for an option.
            if (data?.GetDataPresent(DataFormats.FileDrop) == true && data.GetData(DataFormats.FileDrop) is string[] files)
            {
                return LaunchRequest.FromDroppedFiles(files);
            }

            if (data?.GetDataPresent(DataFormats.UnicodeText) == true && data.GetData(DataFormats.UnicodeText) is string text)
            {
                return LaunchRequest.FromText(text);
            }
        }
        catch (Exception ex) when (ex is ExternalException or InvalidCastException or InvalidOperationException or ArgumentException or OutOfMemoryException)
        {
            Log.Warn("A drop could not be read: " + ex.GetType().Name);
        }

        return LaunchRequest.Empty;
    }

    // A page reported a drop. The window does what it does for a drop on its navigation bar, except that the cursor could not refuse it:
    // what is not usable is said here.
    private void OnDropped(DroppedContent content)
    {
        if (IsDisposed) return;

        try
        {
            if (content.Request.HasWork) RunRequest(content.Request);
            if (content.Files.Count > 0) _bridge.Share(SharePayload.Files(content.Files));

            if (content.Problems.Count > 0) ShowToast(content.Problems[0]);
            else if (!content.HasWork) ShowToast("Rien à ajouter : dépose un lien magnet ou un fichier .torrent.");
        }
        catch (Exception ex)
        {
            Log.Error("A drop on a page failed", ex);
        }
    }

    private void ShowWindow()
    {
        if (WindowState == FormWindowState.Minimized) NativeMethods.Restore(Handle);
        Show();
        Activate();
    }

    private void Quit()
    {
        _exiting = true;
        Close();
    }

    // The views share one browser process, so each of them reports its loss. None can be revived in place: the window leaves, and the program
    // starts again (see Program), unless the browser had only just started, which means that it would stop again at once.
    private void OnBrowserLost(object? sender, EventArgs e)
    {
        // A window that is leaving anyway has nothing to start again for.
        if (Lost is not null || _exiting || IsDisposed) return;

        var restart = BrowserRecovery.ShouldRestart(Stopwatch.GetElapsedTime(_webStartedAt), MinimumRun());
        Lost = new BrowserLoss(restart, !Visible, _tabs.Current);

        // Nobody is left to ask for the cookies of the C411 tab.
        _cookiesKept = true;

        // Not from inside the event of a view: the views are about to be closed.
        BeginInvoke(() => LeaveAfterLoss(restart));
    }

    private void LeaveAfterLoss(bool restart)
    {
        if (IsDisposed) return;

        if (restart)
        {
            Log.Warn("The web component stopped: Arr411 starts again");
        }
        else
        {
            Log.Error("The web component stopped soon after it started: Arr411 does not start again");
            MessageBox.Show(
                Visible ? this : null,
                "Le composant web d'Arr411 (Microsoft Edge WebView2) s'est arrêté juste après son démarrage.\r\n\r\nArr411 se ferme. Relance-le ; si cela se reproduit, répare ou réinstalle WebView2 Runtime.",
                "Arr411",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }

        Quit();
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

    private void Fail(string text)
    {
        ShowWindow();
        ShowToast(text);
    }

    private void ShowTrayHintOnce()
    {
        if (_services.Ui.TrayHintShown) return;

        _services.Ui.TrayHintShown = true;
        _tray.Hint(TrayHint);
    }

    private async Task CloseAfterCookiesAsync()
    {
        try
        {
            var keep = _c411.KeepSessionCookiesAsync();
            if (await Task.WhenAny(keep, Task.Delay(CookieWaitMilliseconds)) == keep) await keep;
        }
        catch (Exception ex)
        {
            Log.Warn("Cookies of the C411 tab not kept at exit: " + ex.GetType().Name);
        }
        finally
        {
            _cookiesKept = true;
            if (!IsDisposed) Close();
        }
    }

    private void ShowTab(string name, bool tellSpa) => ApplyTab(_tabs.Show(name, tellSpa), name);

    private void ApplyTab(TabChange change, string name)
    {
        if (!Tabs.IsTab(name)) return;

        _nav.Selected = name;
        if (change.Changed)
        {
            var showSpa = name != Tabs.C411;
            _spa.Visible = showSpa;
            _c411.Visible = !showSpa;
            UpdatePresentation();
            if (!showSpa) _c411.EnsureLoaded();
            if (Tabs.IsRemembered(name)) _services.Ui.LastTab = name;
        }

        if (change.TellSpa) _bridge.ShowTab(name);
    }

    // Gives the keyboard to the page of the tab on screen.
    private void FocusContent()
    {
        if (IsDisposed || !Visible) return;

        if (_tabs.Current == Tabs.C411) _c411.FocusContent();
        else _spa.FocusContent();
    }

    private void OnTabInvoked(string tab, bool reselected, bool byKeyboard)
    {
        if (!reselected) ShowTab(tab, tellSpa: true);
        else if (tab == Tabs.C411) ShowC411Menu(byKeyboard);
        else _bridge.ReselectTab(tab);

        // A click moves the keyboard to the page; Enter and Space leave it on the bar, which is where the user is. The menu takes it (see ShowMenu).
        if (!byKeyboard && !(reselected && tab == Tabs.C411)) BeginInvoke(FocusContent);
    }

    private void OnTabAlternate(string tab, bool byKeyboard)
    {
        if (tab == Tabs.C411) ShowC411Menu(byKeyboard);
        else _bridge.Share(SharePayload.Settings(tab));
    }

    private void ShowC411Menu(bool byKeyboard)
    {
        var item = _nav.ItemFor(Tabs.C411);
        ShowMenu(_c411Menu, item, new Point(item.Width, 0), ToolStripDropDownDirection.BelowRight, byKeyboard);
    }

    private void ShowAppMenu(bool byKeyboard)
    {
        var button = _nav.MenuButton;
        ShowMenu(_appMenu, button, new Point(button.Width, button.Height), ToolStripDropDownDirection.AboveRight, byKeyboard);
    }

    // A menu is not a window that the keys go to: it reads them through the control it was opened from, so that control must have the keyboard.
    // The pages are windows of another process, which the menu cannot see: with the keyboard left in one, Escape and a click on the page would
    // leave the menu open. So the menu goes away as soon as its control loses the keyboard, and gives it back to the page when it is over
    // (unless the user came with the keyboard, who stays on the bar).
    private void ShowMenu(ContextMenuStrip menu, Control owner, Point at, ToolStripDropDownDirection direction, bool byKeyboard)
    {
        owner.Focus();

        void Dismiss(object? sender, EventArgs e) => menu.Close(ToolStripDropDownCloseReason.AppFocusChange);

        void Finish(object? sender, ToolStripDropDownClosedEventArgs e)
        {
            owner.LostFocus -= Dismiss;
            menu.Closed -= Finish;
            if (!byKeyboard && owner.Focused && IsHandleCreated && !IsDisposed) BeginInvoke(FocusContent);
        }

        owner.LostFocus += Dismiss;
        menu.Closed += Finish;
        menu.Show(owner, at, direction);
    }

    // Returns true when the shortcut is the app's. The action itself runs once the key press is over, so that it cannot move the focus
    // while the key is still being routed. Reload is the app's on every tab, though it only does something on the C411 one: reloading the
    // web app would lose what is on screen, so the browser must never get the key (Debug builds keep its accelerators).
    private bool OnShortcut(Shortcut shortcut)
    {
        try
        {
            BeginInvoke(() => RunShortcut(shortcut));
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            return false;
        }
    }

    private void RunShortcut(Shortcut shortcut)
    {
        if (IsDisposed) return;

        try
        {
            switch (shortcut.Kind)
            {
                case ShortcutKind.SelectTab when shortcut.Tab is { } tab:
                    SelectByShortcut(tab);
                    break;
                case ShortcutKind.NextTab:
                    SelectByShortcut(Neighbour(1));
                    break;
                case ShortcutKind.PreviousTab:
                    SelectByShortcut(Neighbour(-1));
                    break;
                case ShortcutKind.Settings:
                    _bridge.Share(SharePayload.Settings());
                    break;
                case ShortcutKind.Reload:
                    if (_tabs.Current == Tabs.C411) _c411.Reload();
                    break;
                case ShortcutKind.Back:
                    _ = GoBackAsync();
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Error("A shortcut failed", ex);
        }
    }

    private void SelectByShortcut(string tab)
    {
        if (tab != _tabs.Current) ShowTab(tab, tellSpa: true);
        else if (tab != Tabs.C411) _bridge.ReselectTab(tab);

        FocusContent();
    }

    // The tab after (or before) the current one on the bar; the settings page, which has no tab, goes to the first (or the last).
    private string Neighbour(int step)
    {
        var bar = Tabs.Bar;
        for (var i = 0; i < bar.Count; i++)
        {
            if (bar[i] == _tabs.Current) return bar[(i + step + bar.Count) % bar.Count];
        }

        return step > 0 ? bar[0] : bar[^1];
    }

    private async Task GoBackAsync()
    {
        try
        {
            if (_tabs.Current == Tabs.C411)
            {
                _c411.GoBack();
                return;
            }

            var handled = await _bridge.BackAsync();
            if (!handled && !IsDisposed) ShowTab(_tabs.BackTarget(), tellSpa: true);
        }
        catch (Exception ex)
        {
            Log.Error("Going back failed", ex);
        }
    }

    private void OpenExternal(string url)
    {
        switch (ExternalLinks.Classify(url))
        {
            case LinkKind.C411:
                _c411.Navigate(url);
                ShowTab(Tabs.C411, tellSpa: true);
                break;
            case LinkKind.Magnet:
                _bridge.Share(SharePayload.Text(url));
                break;
            case LinkKind.Web:
                ExternalBrowser.Open(url);
                break;
            default:
                Log.Warn("A link was refused");
                break;
        }
    }

    private void UpdateForeground()
    {
        var on = Visible && WindowState != FormWindowState.Minimized;
        if (on != _foreground)
        {
            _foreground = on;
            _bridge.SetForeground(on);
        }

        UpdatePresentation();
    }

    // Each page is told whether it can be seen: the one of the tab on screen, while the window is on screen. The engine always runs.
    private void UpdatePresentation()
    {
        var onScreen = Visible && WindowState != FormWindowState.Minimized;
        var c411 = _tabs.Current == Tabs.C411;
        _spa.SetPresented(onScreen && !c411);
        _c411.SetPresented(onScreen && c411);
    }

    private void ShowToast(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        var textSize = TextRenderer.MeasureText(text, _toast.Font, new Size(Px(ToastMaxWidth), 0), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
        _toast.Text = text;
        _toast.Size = new Size(textSize.Width + _toast.Padding.Horizontal, textSize.Height + _toast.Padding.Vertical);
        PositionToast();
        _toast.Visible = true;
        _toast.BringToFront();
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private void HideToast()
    {
        _toastTimer.Stop();
        _toast.Visible = false;
    }

    // At the bottom of the page, in the middle of what the bar leaves.
    private void PositionToast()
    {
        var left = _nav.Width;
        var room = Math.Max(0, ClientSize.Width - left);
        _toast.Location = new Point(left + Math.Max(0, (room - _toast.Width) / 2), Math.Max(0, ClientSize.Height - _toast.Height - Px(ToastBottomMargin)));
    }
}
