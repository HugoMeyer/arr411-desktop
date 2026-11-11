namespace Arr411.E2E;

/// <summary>The keys of the window: the tabs, the settings, reloading and going back.</summary>
[Trait("Category", "E2E")]
public sealed class ShortcutTests(ShellApp shell) : IClassFixture<ShellApp>
{
    private AppProcess App => shell.App;

    [Fact]
    public async Task Ctrl_and_a_digit_show_the_tabs_of_the_bar()
    {
        await App.ShowTabAsync("radarr");

        foreach (var (digit, tab) in new[] { (1, "c411"), (3, "sonarr"), (5, "qbit"), (4, "lidarr"), (2, "radarr") })
        {
            await App.PressAsync(Win32.Control, Win32.Digit(digit));
            await App.ExpectTabAsync(tab);
        }
    }

    [Fact]
    public async Task Ctrl_Tab_shows_the_next_tab_and_comes_back_around()
    {
        await App.ShowTabAsync("radarr");

        foreach (var tab in new[] { "sonarr", "lidarr", "qbit", "c411", "radarr" })
        {
            await App.PressAsync(Win32.Control, Win32.Tab);
            await App.ExpectTabAsync(tab);
        }
    }

    [Fact]
    public async Task Ctrl_Shift_Tab_shows_the_previous_tab_and_comes_back_around()
    {
        await App.ShowTabAsync("radarr");

        foreach (var tab in new[] { "c411", "qbit", "lidarr", "sonarr", "radarr" })
        {
            await App.PressAsync(Win32.Control, Win32.Shift, Win32.Tab);
            await App.ExpectTabAsync(tab);
        }
    }

    [Fact]
    public async Task Ctrl_PageDown_and_Ctrl_PageUp_move_through_the_tabs()
    {
        await App.ShowTabAsync("radarr");

        await App.PressAsync(Win32.Control, Win32.PageDown);
        await App.ExpectTabAsync("sonarr");
        await App.PressAsync(Win32.Control, Win32.PageUp);
        await App.ExpectTabAsync("radarr");
        await App.PressAsync(Win32.Control, Win32.PageUp);
        await App.ExpectTabAsync("c411");
    }

    [Fact]
    public async Task The_settings_page_has_no_tab_so_the_tab_keys_go_from_its_ends()
    {
        await App.ShowTabAsync("lidarr");
        await App.PressAsync(Win32.Control, Win32.Comma);
        await App.Page.WaitForAsync("App.state.tab === 'settings'");

        await App.PressAsync(Win32.Control, Win32.Tab);
        await App.ExpectTabAsync("c411");

        await App.PressAsync(Win32.Control, Win32.Comma);
        await App.Page.WaitForAsync("App.state.tab === 'settings'");

        await App.PressAsync(Win32.Control, Win32.Shift, Win32.Tab);
        await App.ExpectTabAsync("qbit");
    }

    [Fact]
    public async Task Ctrl_comma_opens_the_settings_and_Alt_Left_leaves_them()
    {
        await App.ShowTabAsync("sonarr");

        await App.PressAsync(Win32.Control, Win32.Comma);
        await App.Page.WaitForAsync("App.state.tab === 'settings'");

        await App.PressAsync(Win32.Alt, Win32.Left);
        await App.ExpectTabAsync("sonarr");
    }

    [Fact]
    public async Task F5_and_Ctrl_R_reload_the_C411_page()
    {
        await App.ShowTabAsync("c411");
        var site = await App.C411Async();

        foreach (var keys in new[] { new[] { Win32.F5 }, [Win32.Control, Win32.R] })
        {
            await site.EvaluateAsync("window.__beforeReload = true");

            await App.PressAsync(keys);

            await site.WaitForAsync("!window.__beforeReload && document.readyState === 'complete'");
        }
    }

    [Fact]
    public async Task F5_and_Ctrl_R_leave_the_web_app_alone()
    {
        await App.ShowTabAsync("radarr");
        await App.Page.EvaluateAsync("window.__kept = true");

        await App.PressAsync(Win32.F5);
        await App.PressAsync(Win32.Control, Win32.R);
        await Task.Delay(1_500);

        Assert.True(await App.Page.EvaluateAsync<bool>("window.__kept === true"), "The web app was reloaded.");
    }

    [Fact]
    public async Task Alt_Left_and_the_browser_back_key_go_back_in_the_C411_page()
    {
        await App.ShowTabAsync("c411");
        var site = await App.C411Async();
        await site.WaitForAsync("location.pathname === '/'");

        foreach (var keys in new[] { new[] { Win32.Alt, Win32.Left }, [Win32.BrowserBack] })
        {
            // As a click on a link is: a page that changes by itself leaves an entry that the browser skips when going back.
            await site.EvaluateAsync("location.href = '/frame'", userGesture: true);
            await site.WaitForAsync("location.pathname === '/frame'");

            await App.PressAsync(keys);

            await site.WaitForAsync("location.pathname === '/'");
        }
    }

    [Fact]
    public async Task The_shortcuts_work_while_the_keyboard_is_on_the_bar()
    {
        // A second click on the C411 tab opens its menu and keeps the keyboard on the bar; Escape closes the menu.
        await App.ShowTabAsync("c411");
        await App.ClickTabAsync(Bar.C411);
        await App.WaitForWindowCountAsync(2);
        await App.PressAsync(Win32.Escape);
        await App.WaitForWindowCountAsync(1);

        await App.PressAsync(Win32.Control, Win32.Digit(3));

        await App.ExpectTabAsync("sonarr");
    }
}
