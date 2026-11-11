using Microsoft.Win32;

namespace Arr411.Core.Config;

/// <summary>Folders of the Windows user that .NET has no name for.</summary>
internal static class KnownFolders
{
    private const string UserShellFolders = @"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders";
    private const string DownloadsId = "{374DE290-123F-4565-9164-39C4925E467B}";

    /// <summary>The Downloads folder, wherever the user moved it to.</summary>
    public static string Downloads()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(UserShellFolders);
            if (key?.GetValue(DownloadsId, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is string raw && raw.Length > 0)
            {
                var path = Environment.ExpandEnvironmentVariables(raw);
                if (Path.IsPathRooted(path)) return path;
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            // Falls back to the default location.
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }
}
