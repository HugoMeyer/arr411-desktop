namespace Arr411.Desktop;

/// <summary>Colours of the web app (app.css) and of the Android tab bar.</summary>
internal static class Theme
{
    public static readonly Color Background = Color.FromArgb(0x07, 0x0D, 0x0B);
    public static readonly Color Bar = Color.FromArgb(0x0A, 0x14, 0x11);
    public static readonly Color Text = Color.FromArgb(0xE7, 0xF1, 0xED);
    public static readonly Color Muted = Color.FromArgb(0x8F, 0xA3, 0x9B);
    public static readonly Color Accent = Color.FromArgb(0x1F, 0xD3, 0x94);
    public static readonly Color Ink = Color.FromArgb(0x03, 0x14, 0x0D);

    /// <summary>Raised surfaces (the toast, the menus), like --card3 in app.css.</summary>
    public static readonly Color Raised = Color.FromArgb(0x1A, 0x2D, 0x26);

    /// <summary>Hairlines: menu borders and separators.</summary>
    public static readonly Color Line = Color.FromArgb(0x2B, 0x44, 0x3B);

    /// <summary>The entry of a menu under the pointer.</summary>
    public static readonly Color Hover = Color.FromArgb(0x26, 0x3C, 0x33);
}
