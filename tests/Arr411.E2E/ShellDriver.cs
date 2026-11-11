namespace Arr411.E2E;

/// <summary>What the shell tests do to the window of the app: its tabs, and the menus that open from its bar.</summary>
internal static class ShellDriver
{
    private static readonly string[] TabNames = ["c411", "radarr", "sonarr", "lidarr", "qbit"];

    /// <summary>Clicks a tab of the bar (its position in the bar: see <see cref="Bar"/>).</summary>
    public static async Task ClickTabAsync(this AppProcess app, int index, bool right = false)
    {
        var window = await app.WindowAsync();
        var (x, y) = Win32.TabCenter(window, index);
        Win32.Click(window, x, y, right);
    }

    /// <summary>Clicks the menu button at the bottom of the bar.</summary>
    public static async Task ClickMenuAsync(this AppProcess app)
    {
        var window = await app.WindowAsync();
        var (x, y) = Win32.MenuCenter(window);
        Win32.Click(window, x, y);
    }

    /// <summary>
    /// Picks an entry of the menu that is open, with the keys: Home for the first entry, Down to reach the entry's position (the first is 1, the
    /// separators do not count), then Enter. Home matters: the menu opens wherever it opens, and the entry under the cursor of whoever uses the
    /// machine is already selected, so counting from "nothing selected" would land on another entry. For the same reason the app takes the
    /// keyboard first and the keys wait for it: putting a window in front moves the windows under the cursor, and the system then selects
    /// again the entry the cursor rests on, after the keys if they did not wait.
    /// </summary>
    public static async Task ChooseMenuItemAsync(this AppProcess app, int position)
    {
        if (await app.TakeKeyboardAsync()) await Task.Delay(300);

        await app.PressAsync(Win32.Home);
        for (var step = 1; step < position; step++) await app.PressAsync(Win32.Down);
        await app.PressAsync(Win32.Enter);
    }

    /// <summary>Clicks in the page, near its bottom right corner where the pages of the tests have nothing, with the real mouse (see <see cref="Win32.RealClickAsync"/>).</summary>
    public static async Task ClickPageAsync(this AppProcess app)
    {
        var window = await app.WindowAsync();
        if (!Win32.HasKeyboard(window) && !Win32.Foreground(window)) throw new InvalidOperationException("Windows would not put the app in front, so nothing was clicked.");

        var (width, height) = Win32.ClientSize(window);
        await Win32.RealClickAsync(window, width - 60, height - 60);
    }

    /// <summary>The tab on screen: the one the web app says it is on while the web app is visible, C411 when it is not.</summary>
    public static async Task<string> CurrentTabAsync(this AppProcess app)
    {
        if (await app.Page.EvaluateAsync<string>("document.visibilityState") != "visible") return "c411";
        return await app.Page.EvaluateAsync<string>("App.state.tab") ?? "";
    }

    /// <summary>Waits until a tab is the one on screen: its page can be seen and the other one cannot.</summary>
    public static async Task ExpectTabAsync(this AppProcess app, string tab)
    {
        if (tab == "c411")
        {
            var site = await app.C411Async();
            await site.WaitForAsync("document.visibilityState === 'visible'");
            await app.Page.WaitForAsync("document.visibilityState === 'hidden'");
            return;
        }

        await app.Page.WaitForAsync($"App.state.tab === {Js.Quote(tab)} && document.visibilityState === 'visible'");
    }

    /// <summary>Puts a tab on screen with the bar, from wherever the previous test left the app.</summary>
    public static async Task ShowTabAsync(this AppProcess app, string tab)
    {
        await app.CloseMenusAsync();
        if (await app.CurrentTabAsync() != tab) await app.ClickTabAsync(Array.IndexOf(TabNames, tab));
        await app.ExpectTabAsync(tab);
    }

    /// <summary>Closes the menu that is open, if there is one.</summary>
    public static async Task CloseMenusAsync(this AppProcess app)
    {
        for (var attempt = 0; attempt < 5 && Win32.VisibleWindowCount(app.ProcessId) > 1; attempt++)
        {
            await app.PressAsync(Win32.Escape);
            await Task.Delay(300);
        }
    }

    /// <summary>Waits until the app has this many windows on screen: one, or two while a menu is open.</summary>
    public static async Task WaitForWindowCountAsync(this AppProcess app, int count)
    {
        var deadline = Environment.TickCount64 + 10_000;
        while (Environment.TickCount64 < deadline)
        {
            if (Win32.VisibleWindowCount(app.ProcessId) == count) return;
            await Task.Delay(100);
        }

        throw new TimeoutException($"The app never had {count} window(s) on screen:\n{Win32.DescribeVisibleWindows(app.ProcessId)}");
    }
}
