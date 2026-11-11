using System.Globalization;
using Arr411.Core.Logging;

namespace Arr411.Desktop;

/// <summary>
/// The icon of the app in the notification area: where the notifications of the engine show up, a way back to the window, and the menu of the app.
/// The menu belongs to the window, which disposes it after the icon.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    /// <summary>Debug builds only: keeps the notification area untouched, so that the tests do not litter the desktop of the developer.</summary>
    public const string QuietVariable = "ARR411_QUIET";

    // What Windows can hold.
    private const int TitleMax = 63;
    private const int TextMax = 255;
    private const int TipMax = 127;

    private const string Name = "Arr411";

    private readonly NotifyIcon _icon;
    private readonly bool _quiet = IsQuiet();
    private bool _balloonIsHint;

    public TrayIcon(Icon icon, ContextMenuStrip menu)
    {
        _icon = new NotifyIcon { Icon = icon, Text = Name, ContextMenuStrip = menu, Visible = !_quiet };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) Opened?.Invoke(this, EventArgs.Empty);
        };
        _icon.BalloonTipClicked += (_, _) =>
        {
            // The hint is about the icon itself: it leads to the window, not to the downloads.
            if (_balloonIsHint) Opened?.Invoke(this, EventArgs.Empty);
            else NotificationClicked?.Invoke(this, EventArgs.Empty);
        };
    }

    /// <summary>The user wants the window (click on the icon, on the hint).</summary>
    public event EventHandler? Opened;

    /// <summary>The user clicked a notification of the engine.</summary>
    public event EventHandler? NotificationClicked;

    /// <summary>What the tooltip of the icon says: the number of downloads in progress, when there are some.</summary>
    public void SetActive(int downloads)
    {
        var text = downloads > 0
            ? Name + " — " + downloads.ToString(CultureInfo.InvariantCulture) + (downloads == 1 ? " téléchargement en cours" : " téléchargements en cours")
            : Name;
        _icon.Text = Clip(text, TipMax);
    }

    /// <summary>Shows a notification. The log only learns that one was shown: the text names what the user downloads.</summary>
    public void Notify(string title, string text)
    {
        title = Clip(title, TitleMax);
        text = Clip(text, TextMax);
        if (title.Length == 0 && text.Length == 0) return;

        if (text.Length == 0) (title, text) = (Name, title);
        else if (title.Length == 0) title = Name;

        Log.Info("Notification shown");
        _balloonIsHint = false;
        if (!_quiet) _icon.ShowBalloonTip(10_000, title, text, ToolTipIcon.Info);
    }

    /// <summary>Tells the user something about the icon itself (the app goes on in the notification area).</summary>
    public void Hint(string text)
    {
        Log.Info("Tray hint shown");
        _balloonIsHint = true;
        if (!_quiet) _icon.ShowBalloonTip(10_000, Name, Clip(text, TextMax), ToolTipIcon.Info);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }

    private static bool IsQuiet()
    {
#if DEBUG
        return !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(QuietVariable));
#else
        return false;
#endif
    }

    private static string Clip(string value, int max)
    {
        value = value.Trim();
        if (value.Length <= max) return value;

        var length = max - 1;
        if (char.IsHighSurrogate(value[length - 1])) length--;
        return value[..length] + "…";
    }
}
