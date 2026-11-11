using Arr411.Core.Logging;
using Arr411.Core.Platform;

namespace Arr411.Desktop;

/// <summary>
/// What the app tells Windows about itself: that it can open magnet links and .torrent files, and that it starts with the session.
/// A thin layer over <see cref="ShellRegistration"/> that adds what only a running desktop needs (telling Explorer, opening Settings).
/// </summary>
internal sealed class ShellIntegration
{
    /// <summary>Debug builds only: a registry key, under HKEY_CURRENT_USER, that stands for "Software", so that the tests never touch the real one.</summary>
    public const string SoftwareKeyVariable = "ARR411_SOFTWARE_KEY";

    private readonly ShellRegistration _registration;
    private readonly bool _real;

    public ShellIntegration(string exePath)
    {
        var (key, real) = Location();
        _registration = new ShellRegistration(exePath, key);
        _real = real;
    }

    /// <summary>The program Windows has to start.</summary>
    public static string ProgramPath => Environment.ProcessPath ?? Application.ExecutablePath;

    /// <summary>The registration is unreadable: counts as not registered.</summary>
    public bool IsRegistered => Read(() => _registration.IsRegistered);

    public bool StartsWithWindows => Read(() => _registration.StartsWithWindows);

    /// <exception cref="IOException">The registry refused (also <see cref="UnauthorizedAccessException"/> and <see cref="System.Security.SecurityException"/>).</exception>
    public void Register()
    {
        _registration.Register();
        Announce();
    }

    /// <exception cref="IOException">The registry refused (also <see cref="UnauthorizedAccessException"/> and <see cref="System.Security.SecurityException"/>).</exception>
    public void Unregister()
    {
        _registration.Unregister();
        Announce();
    }

    /// <exception cref="IOException">The registry refused (also <see cref="UnauthorizedAccessException"/> and <see cref="System.Security.SecurityException"/>).</exception>
    public void SetStartWithWindows(bool enabled) => _registration.SetStartWithWindows(enabled);

    /// <exception cref="IOException">The registry refused (also <see cref="UnauthorizedAccessException"/> and <see cref="System.Security.SecurityException"/>).</exception>
    public void FollowMove() => _registration.FollowMove();

    /// <summary>Opens the page of Windows where the user chooses which app opens magnet links and .torrent files.</summary>
    public static void OpenDefaultAppsSettings() => ExternalBrowser.Open("ms-settings:defaultapps?registeredAppUser=" + ShellRegistration.AppName);

    private static (string Key, bool Real) Location()
    {
#if DEBUG
        if (Environment.GetEnvironmentVariable(SoftwareKeyVariable) is { Length: > 0 } scratch) return (scratch, false);
#endif
        return ("Software", true);
    }

    // Explorer only needs to hear about the real registry, and a test must not make it refresh.
    private void Announce()
    {
        if (_real) NativeMethods.NotifyAssociationsChanged();
    }

    private static bool Read(Func<bool> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Log.Warn("Registration unreadable: " + ex.GetType().Name);
            return false;
        }
    }
}
