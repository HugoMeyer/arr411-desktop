namespace Arr411.E2E;

/// <summary>
/// The app started with --background, as Windows starts it when the user signs in: no window to get in the way, and everything else running (the
/// downloads are watched, and the web pages are ready for the moment the window is asked for).
/// </summary>
[Trait("Category", "E2E")]
public sealed class BackgroundStartTests(ShellApp shell) : IClassFixture<ShellApp>
{
    private static readonly string Magnet = $"magnet:?xt=urn:btih:{FakeC411.Hash}&dn=Blade.Runner.2049";

    private Task<AppProcess> StartAsync(params string[] args) => AppProcess.StartAsync(ShellApp.Settings(shell.Fake), site: shell.Site, args: args);

    private static async Task AssertPaintedAsync(AppProcess app, string name)
    {
        var colors = 0;
        for (var attempt = 0; attempt < 10 && colors <= 30; attempt++)
        {
            using var picture = await app.CaptureAsync(name);
            colors = WindowCapture.DistinctColors(picture);
        }

        Assert.True(colors > 30, $"The window shows {colors} colours.");
    }

    [Fact]
    public async Task The_app_starts_without_a_window_and_with_everything_else_running()
    {
        await using var app = await StartAsync("--background");

        // The window exists, so that the web pages can load in it, and nobody sees it.
        Assert.NotEqual(IntPtr.Zero, app.Window);
        Assert.False(Win32.IsVisible(app.Window));
        Assert.Equal(0, Win32.VisibleWindowCount(app.ProcessId));

        // The web app has started and the engine runs, as when the window is there.
        Assert.Equal("radarr", await app.Page.EvaluateAsync<string>("App.state.tab"));
        await app.Page.WaitForAsync("document.visibilityState === 'hidden'");
        var engine = await app.EngineAsync();
        Assert.Equal("visible", await engine.EvaluateAsync<string>("document.visibilityState"));

        var log = app.ReadLog();
        Assert.DoesNotContain(" ERROR ", log);
        Assert.DoesNotContain(" WARN ", log);
    }

    [Fact]
    public async Task A_second_start_brings_the_window_to_the_screen_with_its_page_drawn()
    {
        await using var app = await StartAsync("--background");

        Assert.Equal(0, await app.LaunchSecondAsync());

        await app.WindowAsync();
        await app.ExpectTabAsync("radarr");
        await AssertPaintedAsync(app, "back-from-background");
    }

    [Fact]
    public async Task A_second_start_in_the_background_leaves_a_hidden_window_alone()
    {
        await using var app = await StartAsync();
        var window = await app.WindowAsync();
        app.CloseWindow();
        await Wait.UntilAsync(() => !Win32.IsVisible(window), "the window leaving the screen");

        // What Windows does at sign-in when the user had already started the app by hand.
        Assert.Equal(0, await app.LaunchSecondAsync("--background"));

        await Task.Delay(1_000);
        Assert.False(Win32.IsVisible(window));
        Assert.False(app.HasExited);
    }

    [Fact]
    public async Task A_magnet_link_given_with_the_background_option_brings_the_window_with_the_add_sheet()
    {
        await using var app = await StartAsync("--background", "--", Magnet);

        await app.WindowAsync();
        await app.Page.WaitForAsync(AddSheet.ShowingLinks(Magnet));
        await AssertPaintedAsync(app, "background-with-a-magnet");
    }

    [Fact]
    public async Task A_tab_given_with_the_background_option_brings_the_window_on_that_tab()
    {
        await using var app = await StartAsync("--background", "--tab", "lidarr");

        await app.WindowAsync();
        await app.ExpectTabAsync("lidarr");
    }
}
