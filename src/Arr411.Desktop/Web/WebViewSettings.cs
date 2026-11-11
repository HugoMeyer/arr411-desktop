using Arr411.Core.Shell;
using Microsoft.Web.WebView2.Core;

namespace Arr411.Desktop.Web;

/// <summary>What the web views of the window have in common: a browser that only does what the app needs.</summary>
internal static class WebViewSettings
{
    // What the right-click menu keeps: editing commands only (the browser's own entries would let the page be saved, printed...).
    private static readonly HashSet<string> EditingCommands = ["cut", "copy", "paste", "pasteAsPlainText", "selectAll", "undo", "redo", "delete"];

    /// <summary>
    /// Turns a key press that reached a web view into a shortcut of the app. A web view hands over the keys the browser would treat as
    /// accelerators (Ctrl+..., F5, Alt+Left, the browser keys); when <paramref name="handler"/> takes the shortcut, the page never sees the key.
    /// </summary>
    public static void HandleKey(KeyEventArgs e, Func<Shortcut, bool>? handler)
    {
        if (handler is null) return;
        if (Shortcuts.Resolve((int)e.KeyCode, e.Control, e.Shift, e.Alt) is { } shortcut && handler(shortcut)) e.Handled = true;
    }

    /// <summary>Turns off what a browser does for itself and the app has no use for. Developer tools stay in Debug builds.</summary>
    public static void Harden(CoreWebView2Settings settings)
    {
        settings.IsStatusBarEnabled = false;
        settings.IsPasswordAutosaveEnabled = false;
        settings.IsGeneralAutofillEnabled = false;
        settings.IsSwipeNavigationEnabled = false;
        settings.IsBuiltInErrorPageEnabled = false;
#if !DEBUG
        settings.AreDevToolsEnabled = false;
        settings.AreBrowserAcceleratorKeysEnabled = false;
#endif
    }

    /// <summary>
    /// Removes from the right-click menu every entry that is not an editing command or one of <paramref name="extraCommands"/>
    /// (names of the browser's own entries); no menu at all when nothing is left. Debug builds keep the whole menu (Inspect).
    /// </summary>
    public static void RestrictContextMenu(CoreWebView2ContextMenuRequestedEventArgs e, IReadOnlySet<string>? extraCommands = null)
    {
#if DEBUG
        return;
#else
        var items = e.MenuItems;
        for (var i = items.Count - 1; i >= 0; i--)
        {
            var item = items[i];
            if (item.Kind == CoreWebView2ContextMenuItemKind.Separator) continue;
            if (!EditingCommands.Contains(item.Name) && extraCommands?.Contains(item.Name) != true) items.RemoveAt(i);
        }

        // Only separators left: nothing to show.
        e.Handled = items.All(item => item.Kind == CoreWebView2ContextMenuItemKind.Separator);
#endif
    }
}
