using System.Drawing;

namespace Arr411.E2E;

/// <summary>The title bar of the window wears the colours of the app instead of those of the system theme (see NativeMethods.ColorTitleBar).</summary>
[Trait("Category", "E2E")]
public sealed class TitleBarTests(FirstRunApp fixture) : IClassFixture<FirstRunApp>
{
    // Windows 11 is the first version that lets a window choose the colour of its title bar.
    private const int FirstBuildWithColoredTitleBars = 22000;

    // The three buttons of the title bar are 46 dp wide each.
    private const double ButtonsWidth = 3 * 46;
    private const int GlyphPixels = 20;
    private const int LightTolerance = 0x40;

    // The colour of the navigation bar (Theme.Bar), which the title bar goes on with, the one of the text (Theme.Text) and the one of the thin
    // line around the window (Theme.Accent).
    private static readonly Color BarColor = Color.FromArgb(0x0A, 0x14, 0x11);
    private static readonly Color TextColor = Color.FromArgb(0xE7, 0xF1, 0xED);
    private static readonly Color BorderColor = Color.FromArgb(0x1F, 0xD3, 0x94);

    private AppProcess App => fixture.App;

    [Fact]
    public async Task The_title_bar_has_the_colour_of_the_navigation_bar()
    {
        if (!CanColorTitleBars) return;

        using var picture = await App.CaptureTitleBarAsync("title-bar");

        // The middle of the bar holds neither the icon and the title, on the left, nor the buttons, on the right.
        var middle = new Rectangle(picture.Width / 4, 2, picture.Width / 2, picture.Height - 4);
        var painted = WindowCapture.CountNear(picture, middle, BarColor, tolerance: 2);

        Assert.True(painted >= middle.Width * middle.Height * 0.99, $"{painted} of the {middle.Width * middle.Height} pixels in the middle of the title bar have the colour of the app.");
    }

    [Fact]
    public async Task The_window_is_edged_with_the_accent_colour()
    {
        if (!CanColorTitleBars) return;

        using var picture = await App.CaptureTitleBarAsync("title-bar-border");

        // The first line of the picture is the border of the window. Its corners are rounded, so only the middle of the line is looked at.
        var top = new Rectangle(picture.Width / 4, 0, picture.Width / 2, 1);
        var painted = WindowCapture.CountNear(picture, top, BorderColor, tolerance: 2);

        Assert.True(painted >= top.Width * 0.99, $"{painted} of the {top.Width} pixels of the top border of the window have the colour of the accent.");
    }

    [Fact]
    public async Task The_buttons_of_the_title_bar_are_light_on_it()
    {
        if (!CanColorTitleBars) return;

        using var picture = await App.CaptureTitleBarAsync("title-bar-buttons");

        var width = (int)Math.Round(ButtonsWidth * Win32.Scale(await App.WindowAsync()));
        var buttons = new Rectangle(picture.Width - width, 0, width, picture.Height);
        var light = WindowCapture.CountNear(picture, buttons, TextColor, LightTolerance);

        Assert.True(light >= GlyphPixels, $"The buttons of the title bar show {light} light pixels.");
    }

    private static bool CanColorTitleBars => Environment.OSVersion.Version.Build >= FirstBuildWithColoredTitleBars;
}
