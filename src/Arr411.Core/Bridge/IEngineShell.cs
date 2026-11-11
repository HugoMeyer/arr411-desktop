namespace Arr411.Core.Bridge;

/// <summary>What the engine (the extension's background logic, running in a hidden page) can ask the window to do. Called on the UI thread.</summary>
public interface IEngineShell
{
    /// <summary>Shows a system notification (a download finished).</summary>
    void Notify(string title, string text);

    /// <summary>Open a link outside the engine (the browser, the qBittorrent tab...). The shell decides and filters.</summary>
    void OpenExternal(string url);

    /// <summary>An event of the engine for the C411 page (a JSON object, as text).</summary>
    void Emit(string json);
}
