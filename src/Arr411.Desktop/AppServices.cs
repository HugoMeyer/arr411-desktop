using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Arr411.Core.Config;
using Arr411.Core.Diagnostics;
using Arr411.Core.Http;
using Arr411.Core.Logging;
using Arr411.Core.Update;
using Arr411.Desktop.Web;

namespace Arr411.Desktop;

/// <summary>Everything the window needs that is not a view: where the files are, the settings, the network client, the cover cache, the updater, and the watch over the program itself.</summary>
internal sealed class AppServices : IDisposable
{
    private readonly RunSentinel _sentinel;
    private readonly MemoryWatch _memoryWatch;
    private readonly Action<string> _onCrash;

    private AppServices(
        AppPaths paths,
        string version,
        ConfigStore config,
        UiState ui,
        NativeHttp http,
        ImageProxy covers,
        PageFetcher pageFetcher,
        UpdateService updates,
        RunSentinel sentinel,
        PreviousRun? previousRun,
        DateTimeOffset started,
        string? webViewVersion)
    {
        Paths = paths;
        Version = version;
        Config = config;
        Ui = ui;
        Http = http;
        Covers = covers;
        PageFetcher = pageFetcher;
        Updates = updates;
        PreviousRun = previousRun;
        Started = started;
        WebViewVersion = webViewVersion;

        _sentinel = sentinel;
        _memoryWatch = new MemoryWatch(sentinel.Beat);

        // An exception that stops the program leaves a note in running.txt: the next start says that the last run ended with a crash.
        _onCrash = sentinel.Crashed;
        CrashLog.Terminating += _onCrash;
    }

    public AppPaths Paths { get; }

    public string Version { get; }

    public ConfigStore Config { get; }

    public UiState Ui { get; }

    public NativeHttp Http { get; }

    public ImageProxy Covers { get; }

    /// <summary>The downloads the C411 page asks the app to make for it (the fallback of its own fetch).</summary>
    public PageFetcher PageFetcher { get; }

    /// <summary>Keeps the program up to date from its GitHub releases (see <see cref="UpdateService"/>).</summary>
    public UpdateService Updates { get; }

    /// <summary>How the run before this one ended, when it did not end the right way.</summary>
    public PreviousRun? PreviousRun { get; }

    public DateTimeOffset Started { get; }

    public string? WebViewVersion { get; }

    public static AppServices Create(AppPaths paths)
    {
        Directory.CreateDirectory(paths.Root);
        Log.Initialize(paths.LogFile);

        var version = typeof(AppServices).Assembly.GetName().Version!.ToString(3);
        Log.Info("Arr411 Desktop " + version + " starting (process " + Environment.ProcessId + ")");

        var webView = WebEnvironment.RuntimeVersion();
        Log.Info(
            $"{RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture}, {Environment.ProcessorCount} processors), "
            + $"{RuntimeInformation.FrameworkDescription}, WebView2 {webView ?? "not found"}");
        Log.Info("Memory at start: " + MemorySnapshot.Take());

        var previous = RunSentinel.Leftover(paths.SessionFile);
        if (previous is not null) Log.Warn(previous.ToString());

        var started = StartTime();
        var sentinel = new RunSentinel(paths.SessionFile);
        sentinel.Begin(version, Environment.ProcessId, started);

        var config = new ConfigStore(paths.ConfigFile, new DpapiProtector());
        var ui = new UiState(paths.UiStateFile);
        var http = new NativeHttp("Arr411-Desktop/" + version);
        var covers = new ImageProxy(config, paths.CoverCache);
        _ = Task.Run(covers.Prune);

        var updates = new UpdateService(new UpdateOptions
        {
            Paths = paths,
            Config = config,
            CurrentVersion = version,
            ExePath = ShellIntegration.ProgramPath,
            Disabled = UpdateOptions.DisabledByEnvironment(),
#if DEBUG
            AutoApply = false,
#endif
        });
        updates.Initialize();

        return new AppServices(paths, version, config, ui, http, covers, new PageFetcher(), updates, sentinel, previous, started, webView);
    }

    /// <summary>
    /// The program ends the right way, whatever still runs of it (a window that Windows closes at shutdown may not get to dispose anything).
    /// Safe to call twice.
    /// </summary>
    public void EndRun() => _sentinel.End();

    /// <summary>
    /// One text with what is needed to understand a problem without asking for more (see <see cref="DiagnosticReport"/>). It reads files and asks Windows
    /// for its events, which can take seconds: call it away from the thread of the window.
    /// </summary>
    public string BuildDiagnosticReport() => DiagnosticReport.Build(
        new ReportFacts(
            "Arr411 Desktop",
            Version,
            DateTimeOffset.Now,
            Started,
            $"{RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})",
            RuntimeInformation.FrameworkDescription,
            WebViewVersion,
            MemorySnapshot.Take(),
            PreviousRun,
            SettingFacts(),
            WindowsEvents.Read(),
            ChildProcesses.Take()),
        Paths.LogFile,
        Paths.CrashFile);

    /// <summary>Keeps the report next to the log, for the case where the clipboard is not available. False when the file could not be written.</summary>
    public bool SaveDiagnosticReport(string report)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Paths.DiagnosticFile) ?? Paths.Root);
            File.WriteAllText(Paths.DiagnosticFile, report);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("The diagnostic report could not be saved: " + ex.GetType().Name);
            return false;
        }
    }

    public void Dispose()
    {
        _memoryWatch.Dispose();
        CrashLog.Terminating -= _onCrash;
        _sentinel.End();
        Updates.Dispose();
        PageFetcher.Dispose();
        Covers.Dispose();
        Http.Dispose();
    }

    // Whether something is set, never what: the report is made to be sent to someone.
    private List<string> SettingFacts() =>
    [
        "qBittorrent: " + Configured("qbUrl"),
        "Radarr: " + Configured("radarrUrl"),
        "Sonarr: " + Configured("sonarrUrl"),
        "Lidarr: " + Configured("lidarrUrl"),
        "Notification when a download is done: " + OnOff("notifyEnabled", fallback: true),
        "Notification for every torrent: " + OnOff("notifyAll", fallback: false),
        "Badges in the lists: " + OnOff("listBadges", fallback: true),
    ];

    private string Configured(string key) => string.IsNullOrWhiteSpace(Config.GetString(key)) ? "not configured" : "configured";

    private string OnOff(string key, bool fallback) => (Config.GetBool(key) ?? fallback) ? "on" : "off";

    private static DateTimeOffset StartTime()
    {
        try
        {
            using var self = Process.GetCurrentProcess();
            return new DateTimeOffset(self.StartTime);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            return DateTimeOffset.Now;
        }
    }
}
