using Arr411.Core.Config;

namespace Arr411.Core.Shell;

/// <summary>Which tab the app opens on (same rules as the Android app).</summary>
public static class StartTab
{
    private static readonly string[] ServiceKeys = ["qbUrl", "radarrUrl", "sonarrUrl", "lidarrUrl"];

    /// <param name="config">The saved settings.</param>
    /// <param name="lastTab">The tab shown when the app was last used.</param>
    /// <param name="requested">A tab asked for explicitly (command line, second instance...).</param>
    public static string Resolve(IConfigReader config, string? lastTab, string? requested = null)
    {
        if (Tabs.IsTab(requested)) return requested;

        // First launch: nothing is configured yet, so go and configure.
        if (ServiceKeys.All(key => string.IsNullOrWhiteSpace(config.GetString(key)))) return Tabs.Settings;

        var preferred = config.GetString("startTab") ?? Tabs.C411;
        if (preferred == "last") preferred = lastTab ?? Tabs.C411;
        return Tabs.IsTab(preferred) && preferred != Tabs.Settings ? preferred : Tabs.C411;
    }
}
