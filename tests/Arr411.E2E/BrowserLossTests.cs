namespace Arr411.E2E;

/// <summary>
/// What the app does when the browser process behind its views stops, which takes every view with it (none can be revived in place): it starts
/// again, on the tab it was on, unless that browser had only just started (it would stop again at once, and the app would never stop starting).
/// </summary>
[Trait("Category", "E2E")]
public sealed class BrowserLossTests
{
    // The app starts again for a browser that had run for a minute, which a test cannot wait for: this makes any run long enough.
    private static readonly Dictionary<string, string> NoWait = new() { ["ARR411_RESTART_GRACE"] = "0" };

    // What the log says besides the loss itself: a warning for each of the three views that lost the browser, and the one that says what comes next.
    private static IReadOnlyList<string> Unexpected(string log) =>
    [
        .. log.Split('\n')
            .Where(line => line.Contains(" WARN ", StringComparison.Ordinal) || line.Contains(" ERROR ", StringComparison.Ordinal))
            .Where(line => !line.Contains("BrowserProcessExited", StringComparison.Ordinal) && !line.Contains("Arr411 starts again", StringComparison.Ordinal)),
    ];

    [Fact]
    public async Task The_app_starts_again_on_the_tab_it_was_on()
    {
        // Not the tab it starts on, so that coming back to the right one means something.
        await using var app = await AppProcess.StartAsync(args: ["--tab", "qbit"], environment: NoWait);
        await app.ExpectTabAsync("qbit");
        var first = app.ProcessId;

        await app.CrashBrowserAsync();
        Assert.Equal(0, await app.WaitForExitAsync(30_000));

        await using var again = await app.FollowRestartAsync();

        Assert.NotEqual(first, again.ProcessId);
        await again.WindowAsync();
        await again.ExpectTabAsync("qbit");
        Assert.Equal(1, again.CountInLog("The web component stopped: Arr411 starts again"));
        Assert.Equal(2, again.CountInLog(" starting (process "));
        Assert.Empty(Unexpected(again.ReadLog()));
    }

    [Fact]
    public async Task The_app_that_takes_over_is_the_instance_that_the_next_launch_reaches()
    {
        await using var app = await AppProcess.StartAsync(args: ["--tab", "qbit"], environment: NoWait);
        await app.CrashBrowserAsync();
        Assert.Equal(0, await app.WaitForExitAsync(30_000));

        await using var again = await app.FollowRestartAsync();

        // The old process let go of the single-instance mutex before the new one asked for it: this launch is handed to the new one, not started.
        Assert.Equal(0, await again.LaunchSecondAsync("--tab", "sonarr"));
        await again.ExpectTabAsync("sonarr");
        Assert.Equal(2, again.CountInLog(" starting (process "));
    }

    [Fact]
    public async Task A_window_that_was_out_of_sight_stays_out_of_sight()
    {
        await using var app = await AppProcess.StartAsync(args: ["--background"], environment: NoWait);
        Assert.False(Win32.IsVisible(app.Window));
        var tab = await app.Page.EvaluateAsync<string>("App.state.tab") ?? "";
        Assert.NotEmpty(tab);

        await app.CrashBrowserAsync();
        Assert.Equal(0, await app.WaitForExitAsync(30_000));

        await using var again = await app.FollowRestartAsync();

        // The window exists, so that the pages can load in it, and nobody sees it.
        Assert.NotEqual(IntPtr.Zero, again.Window);
        Assert.Equal(0, Win32.VisibleWindowCount(again.ProcessId));
        await again.Page.WaitForAsync("document.visibilityState === 'hidden'");
        Assert.Equal(tab, await again.Page.EvaluateAsync<string>("App.state.tab"));
        Assert.Empty(Unexpected(again.ReadLog()));

        // It comes when it is asked for.
        Assert.Equal(0, await again.LaunchSecondAsync());
        await again.WindowAsync();
        await again.ExpectTabAsync(tab);
    }

    [Fact]
    public async Task The_app_does_not_start_again_when_its_browser_stops_soon_after_it_started()
    {
        await using var app = await AppProcess.StartAsync();

        await app.CrashBrowserAsync();

        // It says so, instead of leaving the user in front of a window that shows nothing.
        await Wait.UntilAsync(() => Win32.Dialog(app.ProcessId) != IntPtr.Zero, "the message about the web component");
        var dialog = Win32.Dialog(app.ProcessId);
        Assert.Contains(Win32.VisibleTexts(dialog), text => text.Contains("WebView2", StringComparison.Ordinal));
        Win32.Post(dialog, Win32.Close);

        Assert.Equal(1, await app.WaitForExitAsync(30_000));

        // Nothing takes over: a browser that stops at every start would otherwise have the app start over and over.
        await Task.Delay(3_000);
        Assert.Equal(1, app.CountInLog(" starting (process "));
        Assert.Equal(1, app.CountInLog("The web component stopped soon after it started"));
        Assert.Equal(0, app.CountInLog("starts again"));
    }
}
