using System.Drawing;
using System.Windows.Forms;
using Arr411.Core.Config;
using Arr411.Core.Shell;

namespace Arr411.E2E;

/// <summary>
/// Where the window opens: where it was the last time, unless that place is out of reach (a screen may have been unplugged since), in which
/// case it is centred. The place is written when the window is closed or hidden, wherever the window is then.
/// </summary>
[Trait("Category", "E2E")]
public sealed class WindowPlacementTests
{
    // The usual and the smallest size of the window, in dp (see MainForm).
    private const int UsualWidth = 1100;
    private const int UsualHeight = 720;
    private const int MinimumWidth = 640;
    private const int MinimumHeight = 480;

    /// <summary>A place well inside the screen, at a size that is not the usual one: what a user who moved and resized the window leaves.</summary>
    private static WindowBounds Somewhere(bool maximized = false)
    {
        var area = Win32.PrimaryWorkArea();
        return new WindowBounds(area.X + 100, area.Y + 60, Math.Min(900, area.Width - 200), Math.Min(640, area.Height - 160), maximized);
    }

    private static (int Left, int Top, int Width, int Height) Frame(WindowBounds bounds) => (bounds.X, bounds.Y, bounds.Width, bounds.Height);

    private static Task<AppProcess> StartAsync(string folder) => AppProcess.StartAsync(dataDir: folder);

    private static void MoveTo(IntPtr window, WindowBounds place) => Win32.Move(window, place.X, place.Y, place.Width, place.Height);

    /// <summary>Runs a test with a data folder of its own, which remembers this place when there is one, and removes the folder after it.</summary>
    private static async Task WithFolderAsync(WindowBounds? remembered, Func<string, Task> test)
    {
        var folder = AppProcess.NewDataDir();
        Directory.CreateDirectory(folder);
        if (remembered is { } place) _ = new UiState(Path.Combine(folder, "ui.json")) { Window = place };
        try
        {
            await test(folder);
        }
        finally
        {
            await AppProcess.WaitForProfileReleaseAsync(folder);
            AppProcess.TryDeleteDirectory(folder);
        }
    }

    private static Task AssertCentredAtUsualSizeAsync(WindowBounds? remembered) =>
        WithFolderAsync(
            remembered,
            async folder =>
            {
                await using var app = await StartAsync(folder);
                var window = await app.WindowAsync();

                var scale = Win32.Scale(window);
                Assert.Equal(((int)Math.Round(UsualWidth * scale), (int)Math.Round(UsualHeight * scale)), Win32.ClientSize(window));

                var (left, top, width, height) = Win32.Bounds(window);
                var frame = new Rectangle(left, top, width, height);
                var area = Screen.FromRectangle(frame).WorkingArea;
                Assert.True(area.Contains(frame), $"The window ({frame}) is not within the screen ({area}).");
                Assert.InRange((left + width / 2) - (area.Left + area.Width / 2), -2, 2);
                Assert.InRange((top + height / 2) - (area.Top + area.Height / 2), -2, 2);
            });

    [Fact]
    public async Task The_window_opens_where_it_was_the_last_time()
    {
        var place = Somewhere();
        await WithFolderAsync(
            place,
            async folder =>
            {
                await using var app = await StartAsync(folder);
                var window = await app.WindowAsync();

                Assert.Equal(Frame(place), Win32.Bounds(window));
                Assert.False(Win32.IsMaximized(window));
            });
    }

    [Fact]
    public async Task Moving_and_resizing_the_window_is_remembered_for_the_next_time()
    {
        var place = Somewhere();
        await WithFolderAsync(
            null,
            async folder =>
            {
                await using (var first = await StartAsync(folder))
                {
                    MoveTo(await first.WindowAsync(), place);

                    Assert.Equal(0, await first.CloseAsync());
                    Assert.Equal<WindowBounds?>(place, first.ReadUiState().Window);
                }

                await AppProcess.WaitForProfileReleaseAsync(folder);
                await using var second = await StartAsync(folder);

                Assert.Equal(Frame(place), Win32.Bounds(await second.WindowAsync()));
            });
    }

    [Fact]
    public async Task The_first_window_is_centred_at_its_usual_size()
    {
        await AssertCentredAtUsualSizeAsync(null);
    }

    [Fact]
    public async Task A_window_whose_screen_is_gone_opens_centred_at_its_usual_size()
    {
        // Nothing of it is on any screen now: it was left on one that is not plugged in any more.
        await AssertCentredAtUsualSizeAsync(new WindowBounds(50_000, 50_000, 900, 640, false));
    }

    [Fact]
    public async Task A_window_with_only_a_corner_of_its_title_bar_in_sight_opens_centred_at_its_usual_size()
    {
        // A few pixels of it are on the screen, not enough to grab it by.
        var area = Win32.PrimaryWorkArea();
        await AssertCentredAtUsualSizeAsync(new WindowBounds(area.Right - 40, area.Y + 100, 900, 640, false));
    }

    [Fact]
    public async Task A_window_bigger_than_its_screen_is_brought_down_to_the_size_of_the_screen()
    {
        var area = Win32.PrimaryWorkArea();
        await WithFolderAsync(
            new WindowBounds(area.X + 20, area.Y + 20, area.Width + 700, area.Height + 500, false),
            async folder =>
            {
                await using var app = await StartAsync(folder);

                Assert.Equal((area.X + 20, area.Y + 20, area.Width, area.Height), Win32.Bounds(await app.WindowAsync()));
            });
    }

    [Fact]
    public async Task A_window_smaller_than_the_smallest_the_app_allows_is_made_that_size()
    {
        var area = Win32.PrimaryWorkArea();
        await WithFolderAsync(
            new WindowBounds(area.X + 100, area.Y + 100, 200, 150, false),
            async folder =>
            {
                await using var app = await StartAsync(folder);
                var window = await app.WindowAsync();

                var scale = Win32.Scale(window);
                var (_, _, width, height) = Win32.Bounds(window);
                Assert.Equal(((int)Math.Round(MinimumWidth * scale), (int)Math.Round(MinimumHeight * scale)), (width, height));
            });
    }

    [Fact]
    public async Task A_maximized_window_opens_maximized()
    {
        await WithFolderAsync(
            Somewhere(maximized: true),
            async folder =>
            {
                await using var app = await StartAsync(folder);

                Assert.True(Win32.IsMaximized(await app.WindowAsync()));
            });
    }

    [Fact]
    public async Task A_window_that_opened_maximized_goes_back_to_where_it_was_when_it_is_restored()
    {
        var place = Somewhere(maximized: true);
        await WithFolderAsync(
            place,
            async folder =>
            {
                await using var app = await StartAsync(folder);
                var window = await app.WindowAsync();
                Assert.True(Win32.IsMaximized(window));

                Win32.Unmaximize(window);
                await Wait.UntilAsync(() => !Win32.IsMaximized(window), "the window being restored");

                Assert.Equal(Frame(place), Win32.Bounds(window));
            });
    }

    [Fact]
    public async Task A_window_that_is_closed_while_maximized_keeps_the_place_it_has_when_it_is_not()
    {
        var place = Somewhere(maximized: true);
        await WithFolderAsync(
            place,
            async folder =>
            {
                await using var app = await StartAsync(folder);
                Assert.True(Win32.IsMaximized(await app.WindowAsync()));

                Assert.Equal(0, await app.CloseAsync());

                Assert.Equal<WindowBounds?>(place, app.ReadUiState().Window);
            });
    }

    [Fact]
    public async Task Maximizing_the_window_is_remembered_with_the_place_it_had_before()
    {
        var place = Somewhere();
        await WithFolderAsync(
            null,
            async folder =>
            {
                await using (var first = await StartAsync(folder))
                {
                    var window = await first.WindowAsync();
                    MoveTo(window, place);
                    Win32.Maximize(window);
                    await Wait.UntilAsync(() => Win32.IsMaximized(window), "the window being maximized");

                    Assert.Equal(0, await first.CloseAsync());
                    Assert.Equal<WindowBounds?>(place with { Maximized = true }, first.ReadUiState().Window);
                }

                await AppProcess.WaitForProfileReleaseAsync(folder);
                await using var second = await StartAsync(folder);

                Assert.True(Win32.IsMaximized(await second.WindowAsync()));
            });
    }

    [Fact]
    public async Task A_minimized_window_is_remembered_at_its_place_and_not_where_the_system_parks_it()
    {
        var place = Somewhere();
        await WithFolderAsync(
            null,
            async folder =>
            {
                await using var app = await StartAsync(folder);
                var window = await app.WindowAsync();
                MoveTo(window, place);
                Win32.Minimize(window);
                await Wait.UntilAsync(() => Win32.IsMinimized(window), "the window being minimized");

                Assert.Equal(0, await app.CloseAsync());

                Assert.Equal<WindowBounds?>(place, app.ReadUiState().Window);
            });
    }

    [Fact]
    public async Task A_maximized_window_that_is_minimized_is_still_remembered_as_maximized()
    {
        var place = Somewhere();
        await WithFolderAsync(
            null,
            async folder =>
            {
                await using var app = await StartAsync(folder);
                var window = await app.WindowAsync();
                MoveTo(window, place);
                Win32.Maximize(window);
                await Wait.UntilAsync(() => Win32.IsMaximized(window), "the window being maximized");
                Win32.Minimize(window);
                await Wait.UntilAsync(() => Win32.IsMinimized(window), "the window being minimized");

                Assert.Equal(0, await app.CloseAsync());

                Assert.Equal<WindowBounds?>(place with { Maximized = true }, app.ReadUiState().Window);
            });
    }

    [Fact]
    public async Task Hiding_the_window_in_the_notification_area_remembers_its_place_at_once()
    {
        var place = Somewhere();
        await WithFolderAsync(
            null,
            async folder =>
            {
                await using var app = await StartAsync(folder);
                var window = await app.WindowAsync();
                MoveTo(window, place);

                app.CloseWindow();
                await Wait.UntilAsync(() => !Win32.IsVisible(window), "the window leaving the screen");

                // The app goes on, and a session that ends before it does loses nothing.
                Assert.False(app.HasExited);
                Assert.Equal<WindowBounds?>(place, app.ReadUiState().Window);
            });
    }
}
