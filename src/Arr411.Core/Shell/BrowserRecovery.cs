namespace Arr411.Core.Shell;

/// <summary>
/// What the program does when the browser process behind its web views stops (the views share one, so all of them die with it and none can be
/// revived in place): it starts again, on the tab the user was on. Unless the browser had only just started, in which case it would stop
/// again at once and the program would start again forever.
/// </summary>
public static class BrowserRecovery
{
    /// <summary>How long the browser must have run for its stop to be taken as an accident, not as a fault that comes back at every start.</summary>
    public static readonly TimeSpan MinimumRun = TimeSpan.FromMinutes(1);

    /// <param name="ran">How long the web views had been up when the browser stopped.</param>
    /// <param name="minimum">Replaces <see cref="MinimumRun"/>.</param>
    public static bool ShouldRestart(TimeSpan ran, TimeSpan? minimum = null) => ran >= (minimum ?? MinimumRun);

    /// <summary>
    /// The command line of the program that takes over. A window that was out of sight stays out of sight (and a tab on the command line
    /// would bring it back); otherwise the window comes back on the tab it was on.
    /// </summary>
    /// <param name="hidden">Whether the window was out of sight (in the notification area).</param>
    /// <param name="tab">The tab on screen.</param>
    public static IReadOnlyList<string> RestartArguments(bool hidden, string? tab)
    {
        if (hidden) return ["--background"];

        return Tabs.IsTab(tab) ? ["--tab", tab] : [];
    }
}
