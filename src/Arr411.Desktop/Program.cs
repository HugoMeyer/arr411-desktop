using System.ComponentModel;
using System.Diagnostics;
using System.Security;
using Arr411.Core.Config;
using Arr411.Core.Logging;
using Arr411.Core.Platform;
using Arr411.Core.Shell;
using Arr411.Desktop.Web;

namespace Arr411.Desktop;

internal static class Program
{
    // The exit codes: 0 done, 1 failed (or the web component is missing), 3 another instance is there and does not answer.
    private const int Done = 0;
    private const int Failed = 1;
    private const int Unreachable = 3;

    private static readonly TimeSpan HandOverTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan QuitTimeout = TimeSpan.FromSeconds(15);

    [STAThread]
    private static int Main(string[] args)
    {
        var directory = Environment.CurrentDirectory;
        var request = LaunchRequest.Parse(args, directory);

        // Registering needs no window, no log and no other instance (the installer of a user may run it while the app is open).
        if (request.Register || request.Unregister) return ChangeRegistration(request);

        var paths = AppPaths.FromEnvironment();
        var name = SingleInstance.NameFor(paths.Root);
        if (request.Quit) return Quit(name, args, directory);

        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Log.Error("Unhandled UI exception", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("Unhandled exception", e.ExceptionObject as Exception);

        var gate = SingleInstance.TryAcquire(name);
        if (gate is null)
        {
            // Another instance runs: it gets what this launch is for (a magnet link, a file, or just "come to the front").
            NativeMethods.AllowAnyProcessToForeground();
            if (SingleInstance.ForwardAsync(name, args, directory, HandOverTimeout).GetAwaiter().GetResult()) return Done;

            // It may have just left: then this one is the instance.
            gate = SingleInstance.TryAcquire(name);
            if (gate is null)
            {
                if (!request.Background)
                {
                    MessageBox.Show("Arr411 est déjà lancé mais ne répond pas.", "Arr411", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }

                return Unreachable;
            }
        }

        // The mutex must stay alive as long as the window does: it is what the other launches look for.
        BrowserLoss? lost;
        int code;
        using (gate) code = Run(name, paths, request, out lost);

        // The program that takes over starts only now that the mutex is free: it would take this one for a running instance.
        if (lost is not { } loss) return code;

        return loss.Restart ? StartAgain(loss) : Failed;
    }

    private static int Run(string name, AppPaths paths, LaunchRequest request, out BrowserLoss? lost)
    {
        lost = null;
        if (!WebEnvironment.IsRuntimeInstalled())
        {
            var open = MessageBox.Show(
                "Arr411 a besoin du composant Microsoft Edge WebView2 Runtime, qui n'est pas installé sur ce PC.\r\n\r\nOuvrir la page de téléchargement ?",
                "Arr411",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);
            if (open == DialogResult.Yes) ExternalBrowser.Open(WebEnvironment.RuntimeDownloadUrl);
            return Failed;
        }

        using var services = AppServices.Create(paths);
        FollowMove(paths);

        // The window is closed, hence disposed, by the time Run returns.
        var form = new MainForm(services, request);
        using var listener = new InstanceListener(name, form.Post);
        listener.Start();
        form.Post(request);
        Application.Run(form);
        lost = form.Lost;
        return Done;
    }

    // The browser process behind the views stopped, and took the views with it: the program starts again, on the tab it was on (see BrowserRecovery).
    private static int StartAgain(BrowserLoss loss)
    {
        try
        {
            // The window that comes back has to be able to take the front, as the one that leaves was in it.
            if (!loss.Hidden) NativeMethods.AllowAnyProcessToForeground();

            var start = new ProcessStartInfo(ShellIntegration.ProgramPath) { UseShellExecute = false };
            foreach (var argument in BrowserRecovery.RestartArguments(loss.Hidden, loss.Tab)) start.ArgumentList.Add(argument);

            using var started = Process.Start(start);
            return Done;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            Log.Error("Arr411 could not start again after the web component stopped", ex);
            return Failed;
        }
    }

    private static int ChangeRegistration(LaunchRequest request)
    {
        try
        {
            var shell = new ShellIntegration(ShellIntegration.ProgramPath);
            if (request.Unregister) shell.Unregister();
            else shell.Register();
            return Done;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            return Failed;
        }
    }

    // Asks the running instance to leave and waits for it to be gone.
    private static int Quit(string name, string[] args, string directory)
    {
        if (!SingleInstance.IsRunning(name)) return Done;
        if (!SingleInstance.ForwardAsync(name, args, directory, HandOverTimeout).GetAwaiter().GetResult()) return Unreachable;

        return SingleInstance.WaitUntilGoneAsync(name, QuitTimeout).GetAwaiter().GetResult() ? Done : Unreachable;
    }

    // The registrations hold the path of the program: they follow it when the user moved the folder. A test or portable data folder never touches them.
    private static void FollowMove(AppPaths paths)
    {
        if (paths.IsCustomRoot) return;

        try
        {
            new ShellIntegration(ShellIntegration.ProgramPath).FollowMove();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            Log.Warn("The registrations could not follow the program: " + ex.GetType().Name);
        }
    }
}
