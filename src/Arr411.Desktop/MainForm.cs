using Arr411.Core.Bridge;
using Arr411.Core.Diagnostics;
using Arr411.Core.Logging;
using Arr411.Core.Shell;
using Arr411.Desktop.Nav;

namespace Arr411.Desktop;

/// <summary>
/// The window. A tab bar on the left; on the right the web app (every tab but C411), which answers its native calls here, or the C411 site
/// with the scripts that send a torrent to qBittorrent; behind them, the hidden engine that watches the downloads. It lives on in the
/// notification area, and answers the views and the menus through the parts it is made of: <see cref="WebHost"/> (the views and their
/// bridges), <see cref="LaunchIntake"/> (launches of the program and drops), <see cref="AppMenuActions"/> (what the menus do) and
/// <see cref="AppNotices"/> (what the background tells the user).
/// </summary>
internal sealed class MainForm : Form, ISpaShell, IEngineShell, IPageShell, IMainWindow
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
    private readonly TabState _tabs = new();
    private readonly NavBar _nav = new();
    private readonly WebHost _web;
    private readonly LaunchIntake _intake;
    private readonly AppMenuActions _actions;
    private readonly AppNotices _notices;
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

    private HangWatch? _hangWatch;
    private Font? _toastFont;
    private int _toastFontDpi;
    private bool _foreground;
    private bool _startHidden;
    private bool _webStarted;
    private bool _exiting;
    private bool _cookiesKept;
    private bool _maximized;

    public MainForm(AppServices services, LaunchRequest request)
    {
        _services = services;
        _startHidden = request.Background;
        _foreground = !request.Background;

        AutoScaleMode = AutoScaleMode.None;
        Text = "Arr411";
        BackColor = Theme.Background;
        ForeColor = Theme.Text;
        Icon = LoadIcon(null);
        AllowDrop = true;

        _web = new WebHost(services, this, _tabs, spaShell: this, engineShell: this, pageShell: this, openExternal: OpenExternal);
        _intake = new LaunchIntake(this, _web);
        if (request.Background) _web.Bridge.SetForeground(false);

        // The views that fill the window go in front: the control at index 0 docks last and takes what the bar leaves.
        // The engine goes last, so that it stays behind everything.
        Controls.Add(_web.Spa);
        Controls.Add(_web.C411);
        Controls.Add(_toast);
        Controls.Add(_nav);
        Controls.Add(_web.Engine);
        _toastTimer.Tick += (_, _) => HideToast();
        ApplyDpi();
        RestorePlacement();

        _nav.TabInvoked += OnTabInvoked;
        _nav.TabAlternate += OnTabAlternate;
        _nav.MenuRequested += ShowAppMenu;
        _web.Spa.ShortcutHandler = OnShortcut;
        _web.C411.ShortcutHandler = OnShortcut;
        _web.BrowserLost += OnBrowserLost;

        _actions = new AppMenuActions(services, this, _web.Bridge);
        _appMenu = AppMenu.Build(_actions, tray: false);
        _trayMenu = AppMenu.Build(_actions, tray: true);
        _c411Menu = AppMenu.BuildC411(
            reload: () =>
            {
                _web.C411.Reload();
                ShowTab(Tabs.C411, tellSpa: true);
            },
            home: () =>
            {
                _web.C411.GoHome();
                ShowTab(Tabs.C411, tellSpa: true);
            },
            hasLink: () => _web.C411.CurrentUrl is not null,
            copyLink: () =>
            {
                if (_web.C411.CurrentUrl is { } link) ((ISpaShell)this).CopyText(link);
            },
            settings: () => _web.Bridge.Share(SharePayload.Settings()));

        _tray = new TrayIcon(LoadIcon(SystemInformation.SmallIconSize) ?? SystemIcons.Application, _trayMenu);
        _tray.Opened += (_, _) => ShowWindow();
        _tray.NotificationClicked += (_, _) =>
        {
            ShowWindow();
            ShowTab(Tabs.Qbit, tellSpa: true);
        };
        _notices = new AppNotices(this, _tray, _web.Bridge);

        // The web app opens on the tab decided here: it is asked for it while it boots.
        var start = StartTab.Resolve(services.Config, services.Ui.LastTab, request.Tab);
        ApplyTab(_tabs.Show(start, tellSpa: true), start);
    }

    /// <summary>
    /// Set when the window closed because the browser process behind its views stopped (all of them died with it, and none can be revived in
    /// place): what the program does next. Null when the window closed for any other reason.
    /// </summary>
    public BrowserLoss? Lost => _web.Loss;

    /// <summary>Hands the window something to do: a launch of the program, from this process or another one. May be called from any thread.</summary>
    public void Post(LaunchRequest request) => _intake.Post(request);

    bool IMainWindow.IsClosed => IsDisposed;

    bool IMainWindow.IsLeaving => _exiting || IsDisposed;

    bool IMainWindow.OnScreen => Visible && WindowState != FormWindowState.Minimized;

    IWin32Window? IMainWindow.DialogOwner => Visible ? this : null;

    void IMainWindow.Post(Action action) => BeginInvoke(action);

    void IMainWindow.ShowWindow() => ShowWindow();

    void IMainWindow.Quit() => Quit();

    void IMainWindow.ShowTab(string tab, bool tellSpa) => ShowTab(tab, tellSpa);

    void IMainWindow.Toast(string text) => ShowToast(text);

    void IMainWindow.Hint(string text) => _tray.Hint(text);

    void ISpaShell.SelectTab(string tab) => ShowTab(tab, tellSpa: false);

    void ISpaShell.OpenC411(string url)
    {
        // First the page, then the tab: the tab would otherwise load the home page for nothing.
        _web.C411.Navigate(url);
        ShowTab(Tabs.C411, tellSpa: false);
    }

    void ISpaShell.ReloadC411() => _web.C411.Reload();

    void ISpaShell.OpenExternal(string url) => OpenExternal(url);

    void ISpaShell.Toast(string text) => ShowToast(text);

    void ISpaShell.SetBadge(int count)
    {
        _nav.Badge = count;
        _tray.SetActive(count);
    }

    void ISpaShell.CopyText(string text) => TextClipboard.Copy(text);

    void ISpaShell.Dropped(DroppedContent content) => _intake.DroppedOnApp(content);

    void IEngineShell.Notify(string title, string text) => _notices.Balloon(title, text);

    void IEngineShell.OpenExternal(string url) => OpenExternal(url);

    void IEngineShell.Emit(string json) => _web.Emit(json);

    string IPageShell.UserAgent => _web.C411.UserAgent;

    void IPageShell.Toast(string text) => ShowToast(text);

    Task<string?> IPageShell.CookieHeaderAsync(Uri url) => _web.C411.CookieHeaderAsync(url);

    void IPageShell.Dropped(DroppedContent content) => _intake.DroppedOnPage(content);

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
        _intake.Open();

        if (_webStarted) return;

        _webStarted = true;
        _hangWatch = new HangWatch(job => BeginInvoke(job), Log.Info, message => Log.Warn(message + " | " + MemorySnapshot.Take()));
        BeginInvoke(_web.Start);
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

        // Windows may end the process before anything is disposed: the end of the session is an end like any other, not a stop from outside.
        if (reason == CloseReason.WindowsShutDown) _services.EndRun();

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
        _hangWatch?.Dispose();
        _toastTimer.Dispose();
        _web.Dispose();
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
        drgevent.Effect = _intake.DragEnter(drgevent.Data);
    }

    protected override void OnDragOver(DragEventArgs drgevent)
    {
        base.OnDragOver(drgevent);
        drgevent.Effect = _intake.DragOver();
    }

    protected override void OnDragLeave(EventArgs e)
    {
        base.OnDragLeave(e);
        _intake.DragLeave();
    }

    protected override void OnDragDrop(DragEventArgs drgevent)
    {
        base.OnDragDrop(drgevent);
        _intake.Drop(drgevent.Data);
    }

    private static Icon? LoadIcon(Size? size)
    {
        using var stream = EmbeddedAssets.Open("app.ico");
        if (stream is null) return null;
        return size is { } wanted ? new Icon(stream, wanted) : new Icon(stream);
    }

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

    // None of the views can be revived: the window leaves, and the program starts again (see Program and WebHost.BrowserLost).
    private void OnBrowserLost(BrowserLoss loss)
    {
        // Nobody is left to ask for the cookies of the C411 tab.
        _cookiesKept = true;

        // Not from inside the event of a view: the views are about to be closed.
        BeginInvoke(() => LeaveAfterLoss(loss.Restart));
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
            var keep = _web.C411.KeepSessionCookiesAsync();
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
            _web.Spa.Visible = showSpa;
            _web.C411.Visible = !showSpa;
            _web.UpdatePresentation();
            if (!showSpa) _web.C411.EnsureLoaded();
            if (Tabs.IsRemembered(name)) _services.Ui.LastTab = name;
        }

        if (change.TellSpa) _web.Bridge.ShowTab(name);
    }

    // Gives the keyboard to the page of the tab on screen.
    private void FocusContent()
    {
        if (IsDisposed || !Visible) return;

        if (_tabs.Current == Tabs.C411) _web.C411.FocusContent();
        else _web.Spa.FocusContent();
    }

    private void OnTabInvoked(string tab, bool reselected, bool byKeyboard)
    {
        if (!reselected) ShowTab(tab, tellSpa: true);
        else if (tab == Tabs.C411) ShowC411Menu(byKeyboard);
        else _web.Bridge.ReselectTab(tab);

        // A click moves the keyboard to the page; Enter and Space leave it on the bar, which is where the user is. The menu takes it (see ShowMenu).
        if (!byKeyboard && !(reselected && tab == Tabs.C411)) BeginInvoke(FocusContent);
    }

    private void OnTabAlternate(string tab, bool byKeyboard)
    {
        if (tab == Tabs.C411) ShowC411Menu(byKeyboard);
        else _web.Bridge.Share(SharePayload.Settings(tab));
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
                    SelectByShortcut(_tabs.Neighbour(1));
                    break;
                case ShortcutKind.PreviousTab:
                    SelectByShortcut(_tabs.Neighbour(-1));
                    break;
                case ShortcutKind.Settings:
                    _web.Bridge.Share(SharePayload.Settings());
                    break;
                case ShortcutKind.Reload:
                    if (_tabs.Current == Tabs.C411) _web.C411.Reload();
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
        else if (tab != Tabs.C411) _web.Bridge.ReselectTab(tab);

        FocusContent();
    }

    private async Task GoBackAsync()
    {
        try
        {
            if (_tabs.Current == Tabs.C411)
            {
                _web.C411.GoBack();
                return;
            }

            var handled = await _web.Bridge.BackAsync();
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
                _web.C411.Navigate(url);
                ShowTab(Tabs.C411, tellSpa: true);
                break;
            case LinkKind.Magnet:
                _web.Bridge.Share(SharePayload.Text(url));
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
            _web.Bridge.SetForeground(on);
        }

        _web.UpdatePresentation();
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
