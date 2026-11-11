using Arr411.Core.Config;

namespace Arr411.E2E;

/// <summary>
/// The app in the notification area. The close button of the window hides it, and the app goes on; starting it again, or opening a link with it,
/// brings the window back. Unless the app is set to end when its window is closed, or the system asks for the window to be closed.
/// </summary>
[Trait("Category", "E2E")]
public sealed class TrayTests(ShellApp shell) : IClassFixture<ShellApp>
{
    private static readonly string Magnet = $"magnet:?xt=urn:btih:{FakeC411.Hash}&dn=Blade.Runner.2049";

    // An app of its own, with its own window: the window of the fixture stays where the other tests left it.
    private Task<AppProcess> StartAsync(string? dataDir = null) => AppProcess.StartAsync(ShellApp.Settings(shell.Fake), dataDir, shell.Site);

    private static async Task HideAsync(AppProcess app)
    {
        var window = await app.WindowAsync();
        app.CloseWindow();
        await Wait.UntilAsync(() => !Win32.IsVisible(window), "the window leaving the screen");
    }

    private static void AssertCleanLog(AppProcess app)
    {
        var log = app.ReadLog();
        Assert.DoesNotContain(" ERROR ", log);
        Assert.DoesNotContain(" WARN ", log);
    }

    [Fact]
    public async Task The_close_button_hides_the_window_and_the_app_goes_on_without_it()
    {
        await using var app = await StartAsync();
        await app.ShowTabAsync("c411");
        var site = await app.C411Async();
        var engine = await app.EngineAsync();

        await HideAsync(app);

        // The app is still there, with nothing on screen.
        await Task.Delay(1_000);
        Assert.False(app.HasExited);
        Assert.Equal(0, Win32.VisibleWindowCount(app.ProcessId));

        // Nobody sees the pages, so they are told so and stop drawing. The engine, which watches the downloads, is not one of them.
        await app.Page.WaitForAsync("document.visibilityState === 'hidden'");
        await site.WaitForAsync("document.visibilityState === 'hidden'");
        Assert.Equal("visible", await engine.EvaluateAsync<string>("document.visibilityState"));

        // The place of the window is kept, and the user is told once where the app went.
        await Wait.UntilAsync(() => app.ReadUiState().TrayHintShown, "the user being told about the notification area");
        Assert.NotNull(app.ReadUiState().Window);
        AssertCleanLog(app);
    }

    [Fact]
    public async Task Starting_the_app_again_brings_back_the_window_where_it_was_with_its_page()
    {
        await using var app = await StartAsync();
        await app.ShowTabAsync("qbit");
        var before = Win32.Bounds(await app.WindowAsync());
        await HideAsync(app);

        Assert.Equal(0, await app.LaunchSecondAsync());

        var window = await app.WindowAsync();
        await app.ExpectTabAsync("qbit");
        Assert.Equal(before, Win32.Bounds(window));

        // The page is drawn again, not a blank window.
        var colors = 0;
        for (var attempt = 0; attempt < 10 && colors <= 30; attempt++)
        {
            using var picture = await app.CaptureAsync("back-from-the-notification-area");
            colors = WindowCapture.DistinctColors(picture);
        }

        Assert.True(colors > 30, $"The window shows {colors} colours.");
    }

    [Fact]
    public async Task The_C411_tab_is_shown_again_when_the_window_comes_back()
    {
        await using var app = await StartAsync();
        await app.ShowTabAsync("c411");
        await HideAsync(app);

        Assert.Equal(0, await app.LaunchSecondAsync());

        await app.WindowAsync();
        await app.ExpectTabAsync("c411");
    }

    [Fact]
    public async Task A_magnet_link_brings_the_window_back_with_the_add_sheet()
    {
        await using var app = await StartAsync();
        await HideAsync(app);

        Assert.Equal(0, await app.LaunchSecondAsync("--", Magnet));

        await app.WindowAsync();
        await app.Page.WaitForAsync(AddSheet.ShowingLinks(Magnet));
    }

    [Fact]
    public async Task The_quit_command_ends_the_app_while_its_window_is_hidden()
    {
        await using var app = await StartAsync();
        await HideAsync(app);

        Assert.Equal(0, await app.LaunchSecondAsync("--quit"));

        Assert.Equal(0, await app.WaitForExitAsync());
        AssertCleanLog(app);
    }

    [Fact]
    public async Task The_close_button_ends_the_app_when_it_is_set_not_to_stay_in_the_notification_area()
    {
        var folder = AppProcess.NewDataDir();
        Directory.CreateDirectory(folder);
        _ = new UiState(Path.Combine(folder, "ui.json")) { CloseToTray = false };
        try
        {
            await using var app = await StartAsync(folder);

            app.CloseWindow();

            Assert.Equal(0, await app.WaitForExitAsync());
            AssertCleanLog(app);
        }
        finally
        {
            await AppProcess.WaitForProfileReleaseAsync(folder);
            AppProcess.TryDeleteDirectory(folder);
        }
    }

    [Fact]
    public async Task A_close_the_system_asks_for_ends_the_app_wherever_it_would_live_otherwise()
    {
        await using var app = await StartAsync();
        Assert.True(app.ReadUiState().CloseToTray);

        app.CloseWindowBluntly();

        Assert.Equal(0, await app.WaitForExitAsync());
        AssertCleanLog(app);
    }
}
