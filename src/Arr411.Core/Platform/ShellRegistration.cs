using Microsoft.Win32;

namespace Arr411.Core.Platform;

/// <summary>
/// What Windows has to know to offer Arr411 for magnet links and .torrent files (the "Open with" lists, Settings &gt; Default apps),
/// and to start it with Windows. Everything is written under HKEY_CURRENT_USER: no administrator rights, nothing machine-wide.
/// Nothing is ever made the default behind the user's back: Windows only lets the user choose that.
/// </summary>
public sealed class ShellRegistration
{
    public const string AppName = "Arr411";

    public const string MagnetClass = "Arr411.Magnet";

    public const string TorrentClass = "Arr411.Torrent";

    private const string Description = "Radarr, Sonarr, Lidarr, qBittorrent et C411 dans une seule fenêtre.";

    private readonly string _exe;
    private readonly string _software;

    /// <param name="exePath">The program Windows has to start.</param>
    /// <param name="softwareKey">The key, under HKEY_CURRENT_USER, that stands for "Software". Tests use a scratch one.</param>
    public ShellRegistration(string exePath, string softwareKey = "Software")
    {
        _exe = Path.GetFullPath(exePath);
        _software = softwareKey.Trim('\\');
    }

    /// <summary>The magnet and .torrent handlers are in the registry.</summary>
    public bool IsRegistered => ReadDefault($@"Classes\{MagnetClass}\shell\open\command") is not null;

    /// <summary>The program is started when the user signs in to Windows.</summary>
    public bool StartsWithWindows => ReadValue(RunKey, AppName) is not null;

    /// <summary>
    /// The command Windows runs to open a link or a file. The <c>--</c> ends the options, and only the one argument after it counts
    /// (see <see cref="LaunchRequest.Parse"/>): a link that contains a quote cannot add options, or files to open, to the command line.
    /// </summary>
    private string OpenCommand => $"\"{_exe}\" -- \"%1\"";

    private string RunKey => $@"{_software}\Microsoft\Windows\CurrentVersion\Run";

    private string CapabilitiesKey => $@"{_software}\{AppName}\Capabilities";

    /// <summary>Makes Windows list Arr411 for magnet links and .torrent files. The user still has to choose it as the default.</summary>
    public void Register()
    {
        var classes = _software + @"\Classes";
        var exeName = Path.GetFileName(_exe);

        WriteDefault($@"{classes}\{MagnetClass}", "URL:Arr411 (lien magnet)");
        WriteValue($@"{classes}\{MagnetClass}", "URL Protocol", "");
        WriteDefault($@"{classes}\{MagnetClass}\DefaultIcon", $"\"{_exe}\",0");
        WriteDefault($@"{classes}\{MagnetClass}\shell\open\command", OpenCommand);

        WriteDefault($@"{classes}\{TorrentClass}", "Fichier torrent");
        WriteDefault($@"{classes}\{TorrentClass}\DefaultIcon", $"\"{_exe}\",0");
        WriteDefault($@"{classes}\{TorrentClass}\shell\open\command", OpenCommand);

        using (var progIds = Registry.CurrentUser.CreateSubKey($@"{classes}\.torrent\OpenWithProgids"))
        {
            progIds.SetValue(TorrentClass, Array.Empty<byte>(), RegistryValueKind.None);
        }

        WriteValue($@"{classes}\Applications\{exeName}", "FriendlyAppName", AppName);
        WriteDefault($@"{classes}\Applications\{exeName}\shell\open\command", OpenCommand);
        WriteValue($@"{classes}\Applications\{exeName}\SupportedTypes", ".torrent", "");

        WriteValue(CapabilitiesKey, "ApplicationName", AppName);
        WriteValue(CapabilitiesKey, "ApplicationDescription", Description);
        WriteValue(CapabilitiesKey + @"\URLAssociations", "magnet", MagnetClass);
        WriteValue(CapabilitiesKey + @"\FileAssociations", ".torrent", TorrentClass);
        WriteValue($@"{_software}\RegisteredApplications", AppName, CapabilitiesKey);
    }

    /// <summary>Removes everything this class wrote, the start with Windows included.</summary>
    public void Unregister()
    {
        var classes = _software + @"\Classes";
        DeleteTree($@"{classes}\{MagnetClass}");
        DeleteTree($@"{classes}\{TorrentClass}");
        DeleteTree($@"{classes}\Applications\{Path.GetFileName(_exe)}");
        DeleteTree($@"{_software}\{AppName}");
        DeleteValue($@"{classes}\.torrent\OpenWithProgids", TorrentClass);
        DeleteValue($@"{_software}\RegisteredApplications", AppName);
        DeleteValue(RunKey, AppName);
    }

    public void SetStartWithWindows(bool enabled)
    {
        if (enabled) WriteValue(RunKey, AppName, $"\"{_exe}\" --background");
        else DeleteValue(RunKey, AppName);
    }

    /// <summary>
    /// When the program was moved (what is registered points to a file that is gone), points the registration at its new place.
    /// Never registers anything that was not registered.
    /// </summary>
    public void FollowMove()
    {
        if (ProgramOf(ReadDefault($@"Classes\{MagnetClass}\shell\open\command")) is { } magnet && Moved(magnet)) Register();
        if (ProgramOf(ReadValue(RunKey, AppName)) is { } run && Moved(run)) SetStartWithWindows(true);
    }

    private bool Moved(string registered)
    {
        try
        {
            return !string.Equals(Path.GetFullPath(registered), _exe, StringComparison.OrdinalIgnoreCase) && !File.Exists(registered);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    // The program of a registered command: the quoted part at the start, or the first word.
    private static string? ProgramOf(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;

        command = command.Trim();
        if (command[0] == '"')
        {
            var end = command.IndexOf('"', 1);
            return end > 1 ? command[1..end] : null;
        }

        var space = command.IndexOf(' ');
        return space < 0 ? command : command[..space];
    }

    private string? ReadDefault(string keyUnderSoftware) => ReadValue($@"{_software}\{keyUnderSoftware}", "");

    private static string? ReadValue(string key, string name)
    {
        using var subKey = Registry.CurrentUser.OpenSubKey(key);
        return subKey?.GetValue(name) as string;
    }

    private static void WriteDefault(string key, string value) => WriteValue(key, "", value);

    private static void WriteValue(string key, string name, string value)
    {
        using var subKey = Registry.CurrentUser.CreateSubKey(key);
        subKey.SetValue(name, value, RegistryValueKind.String);
    }

    private static void DeleteTree(string key) => Registry.CurrentUser.DeleteSubKeyTree(key, throwOnMissingSubKey: false);

    private static void DeleteValue(string key, string name)
    {
        using var subKey = Registry.CurrentUser.OpenSubKey(key, writable: true);
        subKey?.DeleteValue(name, throwOnMissingValue: false);
    }
}
