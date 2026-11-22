namespace Arr411.Desktop;

/// <summary>What the parts the window is made of (the intake of launches, the web views, the menus, the notices) ask of the window itself.</summary>
internal interface IMainWindow
{
    /// <summary>The window is disposed: there is nothing left to show anything on.</summary>
    bool IsClosed { get; }

    /// <summary>The window is on its way out (or gone): nothing is worth starting again for it.</summary>
    bool IsLeaving { get; }

    bool Visible { get; }

    /// <summary>The window can be seen: shown, and not minimized.</summary>
    bool OnScreen { get; }

    IntPtr Handle { get; }

    /// <summary>What a dialog belongs to: the window while it is shown, nothing while it is hidden (the dialog would bring it back).</summary>
    IWin32Window? DialogOwner { get; }

    /// <summary>Has the window run <paramref name="action"/> on its thread, once it is free. May be called from any thread; throws when the window cannot take it (no handle yet, closed).</summary>
    void Post(Action action);

    /// <summary>Brings the window to the front, from wherever it was (hidden in the notification area, minimized).</summary>
    void ShowWindow();

    /// <summary>Closes the window for good: the program ends, whatever the setting that keeps it in the notification area says.</summary>
    void Quit();

    void ShowTab(string tab, bool tellSpa);

    /// <summary>Shows a short message on the window.</summary>
    void Toast(string text);

    /// <summary>Shows a short message on the icon of the notification area.</summary>
    void Hint(string text);
}
