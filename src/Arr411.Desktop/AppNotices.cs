using System.Text.Json.Nodes;
using Arr411.Core.Bridge;
using Arr411.Core.Shell;

namespace Arr411.Desktop;

/// <summary>
/// The one place where work of the background (the engine, the service that keeps the program up to date) tells the user
/// something: a balloon of the notification area, or a message <c>{ t: ... }</c> to the web app. Both can be asked from any thread.
/// </summary>
internal sealed class AppNotices
{
    private readonly UiThread _ui;
    private readonly TrayIcon _tray;
    private readonly SpaBridge _spa;

    // Made on the thread of the window, which is the one the icon belongs to.
    public AppNotices(IMainWindow window, TrayIcon tray, SpaBridge spa)
    {
        _ui = new UiThread(window.Post);
        _tray = tray;
        _spa = spa;
    }

    /// <summary>Shows a balloon in the notification area. A click on it brings the window to the downloads tab, or does <paramref name="onClick"/> when given.</summary>
    public void Balloon(string title, string text, Action? onClick = null) => _ui.Run(() => _tray.Notify(title, text, onClick));

    /// <summary>Tells the web app something: <c>{ t: ..., ... }</c>. The page ignores the types it does not know (see Scripts\spa-adapter.js).</summary>
    public void PushToSpa(JsonObject message) => _spa.Push(message);
}
