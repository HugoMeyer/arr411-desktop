using System.Diagnostics.CodeAnalysis;

namespace Arr411.Core.Shell;

/// <summary>The screens of the app. Same identifiers as the Android app and the web app.</summary>
public static class Tabs
{
    public const string C411 = "c411";
    public const string Radarr = "radarr";
    public const string Sonarr = "sonarr";
    public const string Lidarr = "lidarr";
    public const string Qbit = "qbit";

    /// <summary>The settings page of the web app: reachable from every tab, but it has no tab of its own.</summary>
    public const string Settings = "settings";

    /// <summary>The tabs of the navigation bar, top to bottom.</summary>
    public static readonly IReadOnlyList<string> Bar = [C411, Radarr, Sonarr, Lidarr, Qbit];

    public static bool IsTab([NotNullWhen(true)] string? name) =>
        name is C411 or Radarr or Sonarr or Lidarr or Qbit or Settings;

    /// <summary>Whether the app reopens on this tab next time (settings never is).</summary>
    public static bool IsRemembered(string name) => name != Settings;
}
