using Arr411.Core.Shell;

namespace Arr411.Core.Bridge;

/// <summary>What the bridge of the C411 page needs from the window.</summary>
public interface IPageShell
{
    /// <summary>The user agent of the C411 tab. UI thread.</summary>
    string UserAgent { get; }

    /// <summary>Shows a short message. UI thread.</summary>
    void Toast(string text);

    /// <summary>The Cookie header the C411 tab would send to this address (null when it has none). Callable from any thread.</summary>
    Task<string?> CookieHeaderAsync(Uri url);

    /// <summary>
    /// Something was dropped on the C411 page, and the user did it as far as the bridge can tell. The window hands it to the web app (or opens
    /// the C411 link in the tab the user is looking at) but never comes to the front for it: a page may not move the user around. UI thread.
    /// </summary>
    void Dropped(DroppedContent content);
}
