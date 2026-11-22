using System.ComponentModel;
using System.Diagnostics;
using System.Security;
using Arr411.Core.Bridge;
using Arr411.Core.Logging;
using Arr411.Core.Shell;

namespace Arr411.Desktop;

/// <summary>What the menus of the app do (see <see cref="AppMenu"/>): the settings page, the registration with Windows, the logs, the diagnostic report, the about box, quitting.</summary>
internal sealed class AppMenuActions : IAppMenuHost
{
    private readonly AppServices _services;
    private readonly IMainWindow _window;
    private readonly SpaBridge _bridge;
    private readonly ShellIntegration _shell = new(ShellIntegration.ProgramPath);

    public AppMenuActions(AppServices services, IMainWindow window, SpaBridge bridge)
    {
        _services = services;
        _window = window;
        _bridge = bridge;
    }

    public void OpenWindow() => _window.ShowWindow();

    public void OpenSettings()
    {
        _window.ShowWindow();
        _bridge.Share(SharePayload.Settings());
    }

    public void Associate()
    {
        try
        {
            _shell.Register();
        }
        catch (Exception ex) when (IsRegistryFailure(ex))
        {
            Log.Warn("Registration failed: " + ex.GetType().Name);
            Fail("Arr411 n'a pas pu s'enregistrer pour les liens magnet et les fichiers .torrent.");
            return;
        }

        // Windows decides by itself what opens a link: the user has to pick Arr411 in its settings.
        ShellIntegration.OpenDefaultAppsSettings();
        if (_window.Visible) _window.Toast("Arr411 est enregistré : choisissez-le dans les réglages de Windows.");
    }

    public bool StartWithWindows
    {
        get => _shell.StartsWithWindows;
        set
        {
            try
            {
                _shell.SetStartWithWindows(value);
            }
            catch (Exception ex) when (IsRegistryFailure(ex))
            {
                Log.Warn("Start with Windows failed: " + ex.GetType().Name);
                Fail("Impossible de modifier le démarrage avec Windows.");
            }
        }
    }

    public bool CloseToTray
    {
        get => _services.Ui.CloseToTray;
        set => _services.Ui.CloseToTray = value;
    }

    public void OpenLogs() => OpenLogsFolder();

    public void CopyDiagnostics() => _ = CopyDiagnosticsAsync();

    public void ShowAbout()
    {
        var text = "Arr411 Desktop " + _services.Version + "\r\n\r\n"
            + "Radarr, Sonarr, Lidarr, qBittorrent et C411 dans une seule fenêtre.\r\n\r\n"
            + "Licence MIT — © 2026 JoK3RZz\r\n"
            + "https://github.com/HugoMeyer/arr411-desktop";
        MessageBox.Show(_window.DialogOwner, text, "À propos d'Arr411", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    public void Quit() => _window.Quit();

    private static bool IsRegistryFailure(Exception ex) => ex is IOException or UnauthorizedAccessException or SecurityException;

    private void OpenLogsFolder()
    {
        try
        {
            var folder = Path.GetDirectoryName(_services.Paths.LogFile) ?? _services.Paths.Root;
            Directory.CreateDirectory(folder);
            var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            var start = new ProcessStartInfo(explorer) { UseShellExecute = false };
            start.ArgumentList.Add(folder);
            using var process = Process.Start(start);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
        {
            Log.Warn("The logs folder could not be opened: " + ex.GetType().Name);
        }
    }

    // Reading the files and asking Windows for its events can take seconds: the report is made away from the window, which keeps answering.
    private async Task CopyDiagnosticsAsync()
    {
        try
        {
            var (report, saved) = await Task.Run(() =>
            {
                var text = _services.BuildDiagnosticReport();
                return (text, _services.SaveDiagnosticReport(text));
            });
            if (_window.IsClosed) return;

            if (TextClipboard.Copy(report))
            {
                Log.Info("The diagnostic report was copied to the clipboard");
                Tell("Rapport de diagnostic copié : colle-le dans un message.");
                return;
            }

            Log.Warn("The diagnostic report could not be copied to the clipboard");
            if (saved)
            {
                Fail("Le presse-papiers est indisponible : le rapport est dans le fichier diagnostic.txt du dossier des journaux.");
                OpenLogsFolder();
            }
            else
            {
                Fail("Impossible de copier le rapport de diagnostic.");
            }
        }
        catch (Exception ex)
        {
            Log.Error("The diagnostic report could not be made", ex);
            if (!_window.IsClosed) Fail("Impossible de créer le rapport de diagnostic.");
        }
    }

    private void Fail(string text)
    {
        _window.ShowWindow();
        _window.Toast(text);
    }

    // The answer to something the user asked for, where the user is looking: on the window when it is on screen, else on the icon.
    private void Tell(string text)
    {
        if (_window.OnScreen) _window.Toast(text);
        else _window.Hint(text);
    }
}
