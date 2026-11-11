using System.Text.Json;

namespace Arr411.E2E;

/// <summary>
/// The library of the web app is made for a phone. In a window as wide as a screen it still shows posters (or rows) of a reasonable size, as many
/// side by side as fit, and it still loads the whole library as it is scrolled.
/// </summary>
[Trait("Category", "E2E")]
public sealed class LibraryLayoutTests(LibraryApp fixture) : IClassFixture<LibraryApp>, IAsyncLifetime
{
    // The page of a maximized window on an ultra-wide screen (3440 pixels, less the navigation bar of the app), at a scale of 100 %.
    private const int UltraWide = 3363;
    private const int PageHeight = 1300;

    private const string Posters = ".grid > .tile";
    private const string Rows = ".rows > .lrow";

    // How the list on screen is laid out, measured on its first line: its kind, how many items there are side by side, how wide they are,
    // and how much of the line is left empty after the last one. All in the pixels of the page, whatever the scale of the screen.
    private const string MeasureScript = """
        (() => {
          const list = [...document.querySelectorAll('.grid, .rows')].find(l => l.offsetParent !== null && l.children.length > 0);
          if (!list) return null;
          const boxes = [...list.children].map(c => c.getBoundingClientRect());
          const line = boxes.filter(b => Math.abs(b.top - boxes[0].top) < 1);
          return {
            kind: list.className,
            display: getComputedStyle(list).display,
            columns: line.length,
            width: line[0].width,
            free: list.getBoundingClientRect().right - line[line.length - 1].right,
            page: innerWidth
          };
        })()
        """;

    // Goes to the end of the list on screen, which is what loads more of it, and tells how many items it has so far.
    private const string ScrollScript = """
        (() => {
          const items = [...document.querySelectorAll('ITEMS')].filter(i => i.offsetParent !== null);
          const section = items.length > 0 ? items[0].closest('.section') : null;
          if (section) section.scrollTop = section.scrollHeight;
          return items.length;
        })()
        """;

    // The posters on screen have their pictures, so a picture of the window shows them all.
    private const string PostersInViewAreLoaded = """
        [...document.querySelectorAll('.grid > .tile img')]
          .filter(i => i.getBoundingClientRect().top < innerHeight)
          .every(i => i.complete && i.naturalWidth > 0)
        """;

    private sealed record Layout(string Kind, string Display, int Columns, double Width, double Free, int Page);

    private AppProcess App => fixture.App;

    private CdpClient Page => fixture.App.Page;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await Page.SendAsync("Emulation.clearDeviceMetricsOverride");

    [Theory]
    [InlineData(640)]
    [InlineData(1280)]
    [InlineData(1920)]
    [InlineData(UltraWide)]
    public async Task Posters_keep_a_reasonable_size_whatever_the_width_of_the_window(int width)
    {
        await ShowAsync("grid", width);

        var layout = await MeasureAsync();

        Assert.Equal("grid", layout.Kind);
        Assert.True(layout.Columns >= 3, $"The posters are in {layout.Columns} columns.");
        Assert.InRange(layout.Width, 150, 225);
        Assert.InRange(layout.Free, -1, 1);
    }

    [Theory]
    [InlineData(1280)]
    [InlineData(1920)]
    [InlineData(UltraWide)]
    public async Task The_rows_of_the_list_go_side_by_side_in_a_wide_window(int width)
    {
        await ShowAsync("list", width);

        var layout = await MeasureAsync();

        Assert.Equal("rows", layout.Kind);
        Assert.Equal("grid", layout.Display);
        Assert.True(layout.Columns >= 2, $"The rows are in {layout.Columns} columns.");
        Assert.InRange(layout.Width, 380, 580);
        Assert.InRange(layout.Free, -1, 1);
    }

    [Fact]
    public async Task The_rows_of_the_list_stay_in_one_column_in_a_narrow_window()
    {
        await ShowAsync("list", 640);

        var layout = await MeasureAsync();

        Assert.Equal("rows", layout.Kind);
        Assert.Equal(1, layout.Columns);
    }

    [Theory]
    [InlineData("grid")]
    [InlineData("list")]
    public async Task The_whole_library_loads_in_a_very_wide_window(string view)
    {
        await ShowAsync(view, UltraWide);

        var loaded = await ScrollToTheEndAsync(ItemsOf(view));

        Assert.Equal(LibraryApp.FilmCount, loaded);
        Assert.Empty(Page.Problems());
    }

    [Theory]
    [InlineData("grid")]
    [InlineData("list")]
    public async Task A_maximized_window_is_filled_with_the_library(string view)
    {
        var window = await App.WindowAsync();
        Win32.Maximize(window);
        try
        {
            await Wait.UntilAsync(() => Win32.IsMaximized(window), "the window being maximized");
            await Page.WaitForAsync("innerWidth >= screen.availWidth * 0.9", 10_000);
            await SwitchToAsync(view);
            if (view == "grid") await Page.WaitForAsync(PostersInViewAreLoaded, 30_000);

            var layout = await MeasureAsync();
            using var picture = await App.CaptureAsync("library-wide-" + view);

            if (view == "grid")
            {
                Assert.InRange(layout.Width, 150, 225);
                Assert.InRange(layout.Free, -1, 1);
                Assert.True(WindowCapture.CountBandsNear(picture, FakeServices.PosterColor) >= layout.Columns - 1, "The picture of the window shows fewer columns of posters than the page lays out.");
            }
            else if (layout.Page >= 900)
            {
                Assert.True(layout.Columns >= 2, $"The rows are in {layout.Columns} columns.");
                Assert.InRange(layout.Width, 380, 580);
            }
        }
        finally
        {
            Win32.Unmaximize(window);
        }
    }

    private static string ItemsOf(string view) => view == "list" ? Rows : Posters;

    private static string IsShown(string items) => $"[...document.querySelectorAll('{items}')].some(i => i.offsetParent !== null)";

    private async Task ShowAsync(string view, int width)
    {
        await Page.SendAsync("Emulation.setDeviceMetricsOverride", new { width, height = PageHeight, deviceScaleFactor = 1, mobile = false });
        await Page.WaitForAsync($"Math.abs(innerWidth - {width}) <= 1");
        await SwitchToAsync(view);
    }

    // The way a user does it: with the sort and display menu of the library.
    private async Task SwitchToAsync(string view)
    {
        await Page.WaitForAsync(IsShown(Posters) + " || " + IsShown(Rows), 30_000);
        if (await Page.EvaluateAsync<bool>(IsShown(ItemsOf(view)))) return;

        await Page.EvaluateAsync("[...document.querySelectorAll('button[aria-label=\"Trier et affichage\"]')].find(b => b.offsetParent !== null).click()");
        var choice = view == "list" ? "Affichage en liste" : "Affichage en grille";
        var menuItem = $"[...document.querySelectorAll('.menu-item')].find(b => b.textContent.includes('{choice}'))";
        await Page.WaitForAsync(menuItem);
        await Page.EvaluateAsync(menuItem + ".click()");
        await Page.WaitForAsync(IsShown(ItemsOf(view)));
    }

    private async Task<Layout> MeasureAsync()
    {
        var measure = await Page.EvaluateAsync(MeasureScript);
        Assert.Equal(JsonValueKind.Object, measure.ValueKind);
        return new Layout(
            measure.GetProperty("kind").GetString()!,
            measure.GetProperty("display").GetString()!,
            measure.GetProperty("columns").GetInt32(),
            measure.GetProperty("width").GetDouble(),
            measure.GetProperty("free").GetDouble(),
            measure.GetProperty("page").GetInt32());
    }

    private async Task<int> ScrollToTheEndAsync(string items)
    {
        var script = ScrollScript.Replace("ITEMS", items, StringComparison.Ordinal);
        var loaded = 0;
        var deadline = Environment.TickCount64 + 20_000;
        while (loaded < LibraryApp.FilmCount && Environment.TickCount64 < deadline)
        {
            loaded = await Page.EvaluateAsync<int>(script);
            await Task.Delay(100);
        }

        return loaded;
    }
}
