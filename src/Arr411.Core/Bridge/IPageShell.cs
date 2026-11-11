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

    /// <summary>Something was dropped on the C411 page: the window does what it does for a drop on its navigation bar. UI thread.</summary>
    void Dropped(DroppedContent content);
}
