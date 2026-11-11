namespace Arr411.Core.Bridge;

/// <summary>
/// The script that runs in every page of C411: the shim of the Android app (chrome.* and fetch on top of window.QbtPage),
/// the hook that notices when the site is ready, and the extension's content script. The three are the very files of the
/// Android app and of the Chrome extension, unchanged; only their wrapper is ours.
/// </summary>
public static class PageBundle
{
    /// <summary>Set by the first run, so that a page that gets the script twice runs it once.</summary>
    public const string LoadedFlag = "__qbtLoaded";

    /// <param name="shim">shim-page.js: defines chrome and fetch for the content script.</param>
    /// <param name="hook">page-hook.js: flags the page once the site has finished starting.</param>
    /// <param name="content">content.js: adds the qBittorrent buttons to the pages of the site.</param>
    public static string Compose(string shim, string hook, string content) =>
        // Nothing happens without window.QbtPage (the native side only offers it to the pages of C411).
        $"(function(){{ if (!window.QbtPage || window.{LoadedFlag}) return; window.{LoadedFlag} = true;\n"
        + shim + "\n"
        + hook + "\n"
        // The script is injected before the document exists; the content script needs its body.
        + "var __qbtRun = function(){\n" + content + "\n};\n"
        + "if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', __qbtRun, { once: true }); else __qbtRun();\n"
        + "})();";
}
