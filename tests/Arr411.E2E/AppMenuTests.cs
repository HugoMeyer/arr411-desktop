namespace Arr411.E2E;

/// <summary>The menus of the window: the one of the menu button of the bar, and the one of the C411 tab.</summary>
[Trait("Category", "E2E")]
public sealed class AppMenuTests(ShellApp shell) : IClassFixture<ShellApp>
{
    // The entries of the menu of the app, counted from the top as the Down key does (the separators are skipped).
    private const int AppSettings = 1;
    private const int CloseToTray = 4;
    private const int About = 6;
    private const int Quit = 7;

    // The entries of the menu of the C411 tab.
    private const int Reload = 1;
    private const int Home = 2;
    private const int CopyLink = 3;
    private const int C411Settings = 4;

    private AppProcess App => shell.App;

    [Fact]
    public async Task The_menu_button_opens_the_menu_and_Escape_closes_it_and_gives_the_keyboard_back_to_the_page()
    {
        await App.ShowTabAsync("radarr");

        await App.ClickMenuAsync();
        await App.WaitForWindowCountAsync(2);
        Assert.False(await App.Page.EvaluateAsync<bool>("document.hasFocus()"), "The page kept the keyboard while the menu was open.");

        await App.PressAsync(Win32.Escape);
        await App.WaitForWindowCountAsync(1);

        await App.Page.WaitForAsync("document.hasFocus()");
    }

    [Fact]
    public async Task The_C411_tab_opens_its_menu_on_a_second_click_and_on_a_right_click()
    {
        await App.ShowTabAsync("c411");

        await App.ClickTabAsync(Bar.C411);
        await App.WaitForWindowCountAsync(2);
        await App.PressAsync(Win32.Escape);
        await App.WaitForWindowCountAsync(1);

        await App.ClickTabAsync(Bar.C411, right: true);
        await App.WaitForWindowCountAsync(2);
        await App.PressAsync(Win32.Escape);
        await App.WaitForWindowCountAsync(1);
    }

    [Fact]
    public async Task The_C411_menu_reloads_the_page()
    {
        await App.ShowTabAsync("c411");
        var site = await App.C411Async();
        await site.EvaluateAsync("window.__marker = true");

        await App.ClickTabAsync(Bar.C411);
        await App.WaitForWindowCountAsync(2);
        await App.ChooseMenuItemAsync(Reload);

        await site.WaitForAsync("!window.__marker && document.readyState === 'complete'");
        await App.WaitForWindowCountAsync(1);
    }

    [Fact]
    public async Task The_C411_menu_goes_to_the_home_page()
    {
        await App.ShowTabAsync("c411");
        var site = await App.C411Async();
        await site.EvaluateAsync("location.href = '/frame'", userGesture: true);
        await site.WaitForAsync("location.pathname === '/frame'");

        await App.ClickTabAsync(Bar.C411);
        await App.WaitForWindowCountAsync(2);
        await App.ChooseMenuItemAsync(Home);

        await site.WaitForAsync("location.pathname === '/' && document.readyState === 'complete'");
        await App.WaitForWindowCountAsync(1);
    }

    [Fact]
    public async Task The_C411_menu_copies_the_link_of_the_page()
    {
        File.Delete(App.ClipboardSinkFile);
        await App.ShowTabAsync("c411");
        var site = await App.C411Async();
        await site.EvaluateAsync("location.href = '/frame'", userGesture: true);
        await site.WaitForAsync("location.pathname === '/frame'");

        await App.ClickTabAsync(Bar.C411);
        await App.WaitForWindowCountAsync(2);
        await App.ChooseMenuItemAsync(CopyLink);

        await Wait.UntilAsync(() => App.CopiedText() is not null, "the link being copied");
        Assert.Equal("https://c411.org/frame", App.CopiedText());
    }

    [Fact]
    public async Task The_C411_menu_opens_the_settings()
    {
        await App.ShowTabAsync("c411");

        await App.ClickTabAsync(Bar.C411);
        await App.WaitForWindowCountAsync(2);
        await App.ChooseMenuItemAsync(C411Settings);

        await App.Page.WaitForAsync("App.state.tab === 'settings' && document.visibilityState === 'visible'");
    }

    [Fact]
    public async Task A_click_in_the_page_closes_the_menu_and_the_menu_opens_again_afterwards()
    {
        await App.ShowTabAsync("c411");
        var site = await App.C411Async();

        await App.ClickTabAsync(Bar.C411);
        await App.WaitForWindowCountAsync(2);

        await App.ClickPageAsync();

        await App.WaitForWindowCountAsync(1);
        await site.WaitForAsync("document.hasFocus()");

        await App.ClickTabAsync(Bar.C411);
        await App.WaitForWindowCountAsync(2);
        await App.PressAsync(Win32.Escape);
        await App.WaitForWindowCountAsync(1);
    }

    [Fact]
    public async Task The_menu_of_the_app_opens_the_settings()
    {
        await App.ShowTabAsync("radarr");

        await App.ClickMenuAsync();
        await App.WaitForWindowCountAsync(2);
        await App.ChooseMenuItemAsync(AppSettings);

        await App.Page.WaitForAsync("App.state.tab === 'settings' && document.visibilityState === 'visible'");
        await App.WaitForWindowCountAsync(1);
    }

    [Fact]
    public async Task The_menu_of_the_app_switches_the_notification_area_setting_on_and_off()
    {
        Assert.True(App.ReadUiState().CloseToTray);

        try
        {
            await App.ClickMenuAsync();
            await App.WaitForWindowCountAsync(2);
            await App.ChooseMenuItemAsync(CloseToTray);
            await Wait.UntilAsync(() => !App.ReadUiState().CloseToTray, "the setting being switched off");
        }
        finally
        {
            // The other tests of the class share the app: leave it as it was.
            await App.CloseMenusAsync();
            if (!App.ReadUiState().CloseToTray)
            {
                await App.ClickMenuAsync();
                await App.WaitForWindowCountAsync(2);
                await App.ChooseMenuItemAsync(CloseToTray);
            }
        }

        await Wait.UntilAsync(() => App.ReadUiState().CloseToTray, "the setting being switched on again");
    }

    [Fact]
    public async Task The_About_item_shows_a_box_once_the_menu_is_gone()
    {
        await App.ShowTabAsync("radarr");

        await App.ClickMenuAsync();
        await App.WaitForWindowCountAsync(2);
        await App.ChooseMenuItemAsync(About);

        await Wait.UntilAsync(() => Win32.Dialog(App.ProcessId) != IntPtr.Zero, "the about box");
        await Task.Delay(500);
        var windows = Win32.DescribeVisibleWindows(App.ProcessId);
        Assert.Equal(2, Win32.VisibleWindowCount(App.ProcessId));
        Assert.Contains("À propos d'Arr411", windows, StringComparison.Ordinal);

        Win32.Post(Win32.Dialog(App.ProcessId), Win32.Close);
        await App.WaitForWindowCountAsync(1);
    }

    [Fact]
    public async Task The_Quit_item_ends_the_app_without_a_warning()
    {
        await using var app = await AppProcess.StartAsync();

        await app.ClickMenuAsync();
        await app.WaitForWindowCountAsync(2);
        await app.ChooseMenuItemAsync(Quit);

        Assert.Equal(0, await app.WaitForExitAsync());
        var log = app.ReadLog();
        Assert.DoesNotContain(" ERROR ", log, StringComparison.Ordinal);
        Assert.DoesNotContain(" WARN ", log, StringComparison.Ordinal);
    }
}
