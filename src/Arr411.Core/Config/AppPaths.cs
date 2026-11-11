namespace Arr411.Core.Config;

/// <summary>Where the app keeps its files: %LOCALAPPDATA%\Arr411, or the folder named by ARR411_DATA (tests, portable use).</summary>
public sealed class AppPaths
{
    public const string RootVariable = "ARR411_DATA";

    /// <summary>The folder that replaces the Downloads folder of the user (tests, portable use).</summary>
    public const string DownloadsVariable = "ARR411_DOWNLOADS";

    /// <param name="root">The folder of the app's own files.</param>
    /// <param name="downloads">Where the files saved from the C411 tab go; a folder under the root when not given.</param>
    public AppPaths(string root, string? downloads = null)
    {
        Root = Path.GetFullPath(root);
        Downloads = Path.GetFullPath(string.IsNullOrWhiteSpace(downloads) ? Path.Combine(Root, "Downloads") : downloads);
    }

    public string Root { get; }

    /// <summary>The root was chosen with ARR411_DATA instead of being the usual one: a test or a portable copy, which must not touch the system-wide settings of the user.</summary>
    public bool IsCustomRoot { get; private init; }

    /// <summary>Where the files saved from the C411 tab go: the Downloads folder of the user, or the folder named by ARR411_DOWNLOADS.</summary>
    public string Downloads { get; }

    /// <summary>Settings, encrypted for the current Windows user.</summary>
    public string ConfigFile => Path.Combine(Root, "config.dat");

    /// <summary>Window and tab state (not sensitive).</summary>
    public string UiStateFile => Path.Combine(Root, "ui.json");

    public string CoverCache => Path.Combine(Root, "cache", "covers");

    /// <summary>User data folder shared by all the browser views (cookies, local storage).</summary>
    public string WebViewData => Path.Combine(Root, "WebView2");

    public string LogFile => Path.Combine(Root, "logs", "arr411.log");

    public static AppPaths FromEnvironment()
    {
        var custom = Environment.GetEnvironmentVariable(RootVariable);
        var root = string.IsNullOrWhiteSpace(custom)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Arr411")
            : custom;
        var downloads = Environment.GetEnvironmentVariable(DownloadsVariable);
        return new AppPaths(root, string.IsNullOrWhiteSpace(downloads) ? KnownFolders.Downloads() : downloads)
        {
            IsCustomRoot = !string.IsNullOrWhiteSpace(custom),
        };
    }
}
