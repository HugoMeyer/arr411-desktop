using System.Drawing;

namespace Arr411.E2E;

/// <summary>Where things are on the navigation bar, and what they look like (see Nav\NavBar.cs and Theme.cs).</summary>
internal static class Bar
{
    public const int C411 = 0;
    public const int Radarr = 1;
    public const int Sonarr = 2;
    public const int Lidarr = 3;
    public const int Qbit = 4;

    /// <summary>The pill of the current tab: the accent colour at 17 % over the background of the bar.</summary>
    public static readonly Color Pill = Color.FromArgb(14, 52, 39);

    /// <summary>The accent colour: the counter on the qBittorrent tab is filled with it.</summary>
    public static readonly Color Accent = Color.FromArgb(0x1F, 0xD3, 0x94);

    private const double ItemHeight = 64;
    private const double EdgeMargin = 8;
    private const double PillLeft = 11;
    private const double PillTop = 8;
    private const double PillWidth = 54;
    private const double PillHeight = 30;

    /// <summary>The pill of a tab, in pixels of the client area.</summary>
    public static Rectangle PillArea(double scale, int index) => Scaled(scale, PillLeft, EdgeMargin + ItemHeight * index + PillTop, PillWidth, PillHeight);

    /// <summary>The part of the counter of a tab that its icon does not reach, in pixels of the client area.</summary>
    public static Rectangle BadgeArea(double scale, int index) => Scaled(scale, PillLeft + 40, EdgeMargin + ItemHeight * index + PillTop + 2, 9, 14);

    private static Rectangle Scaled(double scale, double x, double y, double width, double height) =>
        new((int)Math.Round(x * scale), (int)Math.Round(y * scale), (int)Math.Round(width * scale), (int)Math.Round(height * scale));
}

/// <summary>The navigation bar on the left of the window: its tabs, its counter and its menu.</summary>
[Trait("Category", "E2E")]
public sealed class NavigationBarTests(ShellApp shell) : IClassFixture<ShellApp>
{
    private const int PillPixels = 400;
    private const int BadgePixels = 20;

    private AppProcess App => shell.App;

    [Fact]
    public async Task A_click_on_a_tab_shows_its_page()
    {
        foreach (var (index, tab) in new[] { (Bar.Sonarr, "sonarr"), (Bar.Lidarr, "lidarr"), (Bar.Qbit, "qbit"), (Bar.Radarr, "radarr") })
        {
            await ClickAsync(index);

            await App.Page.WaitForAsync($"App.state.tab === {Js.Quote(tab)} && document.visibilityState === 'visible'");
        }
    }

    [Fact]
    public async Task A_click_on_the_C411_tab_shows_the_site_instead_of_the_web_app()
    {
        await ClickAsync(Bar.C411);

        var site = await App.C411Async();
        await site.WaitForAsync("document.visibilityState === 'visible'");
        await App.Page.WaitForAsync("document.visibilityState === 'hidden'");

        await ClickAsync(Bar.Radarr);
        await App.Page.WaitForAsync("document.visibilityState === 'visible'");
        await site.WaitForAsync("document.visibilityState === 'hidden'");
    }

    [Fact]
    public async Task The_pill_marks_the_current_tab()
    {
        await ClickAsync(Bar.Sonarr);
        await App.Page.WaitForAsync("App.state.tab === 'sonarr'");

        using var picture = await App.CaptureAsync("bar-sonarr");

        var scale = Win32.Scale(await App.WindowAsync());
        Assert.True(Pixels(picture, scale, Bar.Sonarr) > PillPixels, "The pill is not on the Sonarr tab.");
        foreach (var other in new[] { Bar.C411, Bar.Radarr, Bar.Lidarr, Bar.Qbit })
        {
            Assert.True(Pixels(picture, scale, other) < 10, $"Tab {other} has a pill too.");
        }
    }

    [Fact]
    public async Task The_settings_page_has_no_tab_in_the_bar()
    {
        var scale = Win32.Scale(await App.WindowAsync());

        // With a tab current the same capture shows its pill: the areas are where a pill would be.
        await ClickAsync(Bar.Lidarr);
        await App.Page.WaitForAsync("App.state.tab === 'lidarr'");
        using (var before = await App.CaptureAsync("bar-lidarr"))
        {
            Assert.True(Pixels(before, scale, Bar.Lidarr) > PillPixels, "The pill is not on the Lidarr tab.");
        }

        await ClickAsync(Bar.Lidarr, right: true);
        await App.Page.WaitForAsync("App.state.tab === 'settings'");

        using var picture = await App.CaptureAsync("bar-settings");
        foreach (var index in new[] { Bar.C411, Bar.Radarr, Bar.Sonarr, Bar.Lidarr, Bar.Qbit })
        {
            Assert.True(Pixels(picture, scale, index) < 10, $"Tab {index} has a pill on the settings page.");
        }
    }

    [Fact]
    public async Task The_downloads_in_progress_are_counted_on_the_qBittorrent_tab()
    {
        shell.Fake.Torrents = """
            [{"hash":"aaaa","name":"Un","progress":0.4,"state":"downloading","category":"films"},
             {"hash":"bbbb","name":"Deux","progress":0.1,"state":"downloading","category":"films"},
             {"hash":"cccc","name":"Trois","progress":1,"state":"uploading","category":"films"}]
            """;
        try
        {
            await ClickAsync(Bar.Qbit);
            await App.Page.WaitForAsync("App.state.tab === 'qbit' && App.qbit.store.loaded && Object.keys(App.qbit.store.torrents).length === 3");
            await ClickAsync(Bar.Radarr);
            await App.Page.WaitForAsync("App.state.tab === 'radarr'");

            // Read from the screen: the counter is drawn by the bar itself.
            var scale = Win32.Scale(await App.WindowAsync());
            var counted = 0;
            for (var attempt = 0; attempt < 20 && counted < BadgePixels; attempt++)
            {
                using var picture = await App.CaptureAsync("bar-badge");
                counted = WindowCapture.CountNear(picture, Bar.BadgeArea(scale, Bar.Qbit), Bar.Accent);
            }

            Assert.True(counted >= BadgePixels, $"The counter shows {counted} pixels of its colour.");
        }
        finally
        {
            shell.Fake.Torrents = "[]";
        }
    }

    private async Task ClickAsync(int index, bool right = false)
    {
        var window = await App.WindowAsync();
        var (x, y) = Win32.TabCenter(window, index);
        Win32.Click(window, x, y, right);
    }

    private static int Pixels(Bitmap picture, double scale, int index) => WindowCapture.CountNear(picture, Bar.PillArea(scale, index), Bar.Pill, tolerance: 6);
}
