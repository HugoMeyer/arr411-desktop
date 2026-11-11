using Arr411.Core.Shell;

namespace Arr411.Core.Bridge;

/// <summary>What the web app can ask the window to do (the Android app's JavaScript interface, minus the network and storage). Called on the UI thread.</summary>
public interface ISpaShell
{
    /// <summary>The web app switched tab by itself and wants the window to follow.</summary>
    void SelectTab(string tab);

    /// <summary>Open a page of c411.org in the C411 tab.</summary>
    void OpenC411(string url);

    void ReloadC411();

    /// <summary>Open a link outside the web app (browser, C411 tab, magnet hand-over...). The shell decides and filters.</summary>
    void OpenExternal(string url);

    void Toast(string text);

    /// <summary>The number of active downloads, shown on the qBittorrent tab.</summary>
    void SetBadge(int count);

    void CopyText(string text);

    /// <summary>Something was dropped on the web app: the window does what it does for a drop on its navigation bar.</summary>
    void Dropped(DroppedContent content);
}
