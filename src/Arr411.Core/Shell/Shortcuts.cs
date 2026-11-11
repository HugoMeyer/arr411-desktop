namespace Arr411.Core.Shell;

public enum ShortcutKind
{
    /// <summary>Go to the tab named by <see cref="Shortcut.Tab"/>.</summary>
    SelectTab,

    NextTab,

    PreviousTab,

    /// <summary>One step back (history of the C411 tab, page of the web app).</summary>
    Back,

    /// <summary>Load the C411 page again.</summary>
    Reload,

    Settings,
}

public readonly record struct Shortcut(ShortcutKind Kind, string? Tab = null);

/// <summary>The keyboard shortcuts of the window. The same table serves the native controls and the web views (which report the keys they get).</summary>
public static class Shortcuts
{
    // Windows virtual-key codes.
    private const int VkTab = 0x09;
    private const int VkPageUp = 0x21;
    private const int VkPageDown = 0x22;
    private const int VkLeft = 0x25;
    private const int VkDigit1 = 0x31;
    private const int VkR = 0x52;
    private const int VkNumpad1 = 0x61;
    private const int VkF5 = 0x74;
    private const int VkBrowserBack = 0xA6;
    private const int VkComma = 0xBC;

    /// <summary>The shortcut for a key press, or null when the key means nothing to the window.</summary>
    public static Shortcut? Resolve(int virtualKey, bool ctrl, bool shift, bool alt)
    {
        // AltGr is reported as Ctrl+Alt: those keys type characters, they are never shortcuts.
        if (ctrl && alt) return null;

        if (virtualKey == VkBrowserBack && !ctrl && !shift && !alt) return new Shortcut(ShortcutKind.Back);
        if (virtualKey == VkLeft && alt && !ctrl && !shift) return new Shortcut(ShortcutKind.Back);
        if (alt) return null;

        if (virtualKey == VkF5) return new Shortcut(ShortcutKind.Reload);
        if (!ctrl) return null;

        if (virtualKey == VkTab) return new Shortcut(shift ? ShortcutKind.PreviousTab : ShortcutKind.NextTab);
        if (shift) return null;

        switch (virtualKey)
        {
            case VkR:
                return new Shortcut(ShortcutKind.Reload);
            case VkComma:
                return new Shortcut(ShortcutKind.Settings);
            case VkPageDown:
                return new Shortcut(ShortcutKind.NextTab);
            case VkPageUp:
                return new Shortcut(ShortcutKind.PreviousTab);
        }

        var index = virtualKey switch
        {
            >= VkDigit1 and < VkDigit1 + 5 => virtualKey - VkDigit1,
            >= VkNumpad1 and < VkNumpad1 + 5 => virtualKey - VkNumpad1,
            _ => -1,
        };
        return index >= 0 ? new Shortcut(ShortcutKind.SelectTab, Tabs.Bar[index]) : null;
    }
}
