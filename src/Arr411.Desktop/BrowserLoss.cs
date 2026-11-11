namespace Arr411.Desktop;

/// <summary>What the window leaves behind when it closes because the browser process behind its views stopped (see <see cref="Core.Shell.BrowserRecovery"/>).</summary>
/// <param name="Restart">Whether the program starts again.</param>
/// <param name="Hidden">Whether the window was out of sight (in the notification area).</param>
/// <param name="Tab">The tab that was on screen.</param>
internal readonly record struct BrowserLoss(bool Restart, bool Hidden, string Tab);
