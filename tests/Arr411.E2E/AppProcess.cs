using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Arr411.Core.Config;
using Arr411.Core.Platform;
using Microsoft.Win32;

namespace Arr411.E2E;

/// <summary>A running Arr411.exe with its own data folder, driven through the web view's debug port.</summary>
public sealed class AppProcess : IAsyncDisposable
{
    private const string BootCondition = "document.readyState === 'complete' && window.App && App.state && !!App.state.tab";

    private const string WebAppUrl = "https://arr411.local/";
    private const string EngineUrl = "https://engine.arr411.local/";
    private const string C411Url = "https://c411.org/";
    private const string EngineBootCondition = "document.readyState === 'complete' && !!window.QbtEngine && typeof window.__engineHandle === 'function'";

    /// <summary>How many pages the user can see or use: the web app and the C411 tab (the hidden engine is not one of them).</summary>
    public const int ViewCount = 2;

    // Where the registrations of the tests go: a key of their own stands for "Software", so that the registry of the user is never touched.
    private const string ScratchRoot = @"Software\Arr411Tests";

    private readonly Process _process;
    private readonly bool _ownsDataDir;
    private readonly int _port;
    private readonly IReadOnlyDictionary<string, string> _environment;
    private CdpClient? _engine;
    private CdpClient? _c411;
    private bool _disposed;

    private AppProcess(Process process, string dataDir, bool ownsDataDir, int port, IReadOnlyDictionary<string, string> environment, CdpClient page)
    {
        _process = process;
        DataDir = dataDir;
        _ownsDataDir = ownsDataDir;
        _port = port;
        _environment = environment;
        Page = page;
    }

    public string DataDir { get; }

    public int ProcessId => _process.Id;

    public bool HasExited => _process.HasExited;

    /// <summary>What the process returned. Only meaningful once it has exited.</summary>
    public int ExitCode => _process.ExitCode;

    /// <summary>The window of the app, whether or not it is on screen (it is hidden while the app lives in the notification area). Zero if there is none.</summary>
    public IntPtr Window => Win32.MainWindow(_process.Id);

    /// <summary>What the app remembers between two runs (the last tab, the window position, whether it closes to the notification area), as the file holds it now.</summary>
    public UiState ReadUiState() => new(Path.Combine(DataDir, "ui.json"));

    /// <summary>Where the files saved from the C411 tab go: a folder of the data folder, never the real Downloads folder.</summary>
    public string DownloadsDir => DownloadsOf(DataDir);

    /// <summary>The file the addresses the app would open in the default browser are written to.</summary>
    public string BrowserSinkFile => Path.Combine(DataDir, "browser-sink.txt");

    /// <summary>The file the text the app would put on the clipboard is written to: the tests must not overwrite what the user has copied.</summary>
    public string ClipboardSinkFile => Path.Combine(DataDir, "clipboard-sink.txt");

    /// <summary>The registry key, under HKEY_CURRENT_USER, that stands for "Software" for this app: what it registers for links and files, and for the start with Windows, goes there.</summary>
    public string SoftwareKey => _environment["ARR411_SOFTWARE_KEY"];

    /// <summary>The registrations of the app (magnet links, .torrent files, start with Windows), as the registry of the tests holds them now.</summary>
    public ShellRegistration Registration => new(ExePath, SoftwareKey);

    /// <summary>The scratch registry key that goes with a data folder.</summary>
    public static string ScratchSoftwareKey(string dataDir) => $@"{ScratchRoot}\{Path.GetFileName(dataDir.TrimEnd('\\'))}";

    public CdpClient Page { get; }

    public string ConfigFile => Path.Combine(DataDir, "config.dat");

    public string LogFile => Path.Combine(DataDir, "logs", "arr411.log");

    public static string ExePath =>
        Path.GetFullPath(typeof(AppProcess).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().First(a => a.Key == "DesktopExe").Value!);

    /// <summary>The version the app reports (that of its assembly).</summary>
    public static string ExpectedVersion => AssemblyName.GetAssemblyName(Path.ChangeExtension(ExePath, ".dll")).Version!.ToString(3);

    public static string NewDataDir() => Path.Combine(Path.GetTempPath(), "arr411-e2e-" + Guid.NewGuid().ToString("N")[..8]);

    public static void TryDeleteDirectory(string directory)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(250);
            }
        }
    }

    /// <summary>Starts the app and waits until its web app has booted and its views exist.</summary>
    /// <param name="config">Settings to start with.</param>
    /// <param name="dataDir">A data folder to reuse (the caller then deletes it); a new one, deleted with the instance, by default.</param>
    /// <param name="site">The stand-in for c411.org. Without one the tests still never reach the real site: c411.org leads nowhere.</param>
    /// <param name="sitePort">Where c411.org leads when there is no <paramref name="site"/>: a port nobody listens on, unless a site is started there later.</param>
    /// <param name="args">The command line, as when the system opens a link or a file with the app.</param>
    /// <param name="environment">More environment variables for the process (they win over the ones the harness sets).</param>
    public static async Task<AppProcess> StartAsync(
        JsonObject? config = null,
        string? dataDir = null,
        FakeC411? site = null,
        int? sitePort = null,
        IReadOnlyList<string>? args = null,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var exe = ExePath;
        if (!File.Exists(exe)) throw new FileNotFoundException("Build the Debug configuration of Arr411.Desktop first.", exe);

        var ownsDataDir = dataDir is null;
        dataDir ??= NewDataDir();
        Directory.CreateDirectory(dataDir);
        if (config is not null) new ConfigStore(Path.Combine(dataDir, "config.dat"), new DpapiProtector()).Merge(config, "test");

        var port = Net.FreePort();
        var variables = new Dictionary<string, string>
        {
            ["ARR411_DATA"] = dataDir,
            ["ARR411_CDP_PORT"] = port.ToString(CultureInfo.InvariantCulture),
            ["ARR411_TEST_SITE_PORT"] = (site?.Port ?? sitePort ?? Net.FreePort()).ToString(CultureInfo.InvariantCulture),
            ["ARR411_DOWNLOADS"] = DownloadsOf(dataDir),
            ["ARR411_BROWSER_SINK"] = Path.Combine(dataDir, "browser-sink.txt"),
            ["ARR411_CLIPBOARD_SINK"] = Path.Combine(dataDir, "clipboard-sink.txt"),
            ["ARR411_SOFTWARE_KEY"] = ScratchSoftwareKey(dataDir),
            // No icon in the notification area and no balloons: the tests only read the log.
            ["ARR411_QUIET"] = "1",
            // The tests send in a second what a person sends in a minute, and the tests of a class share one app: with the real window, the
            // limits of the C411 page would run out for the tests that come last.
            ["ARR411_PAGE_RATE_WINDOW"] = "250",
        };
        if (environment is not null)
        {
            foreach (var (name, value) in environment) variables[name] = value;
        }

        var process = Launch(dataDir, variables, args ?? []);

        CdpClient? page = null;
        try
        {
            page = await CdpClient.ConnectAsync(await WaitForPageAsync(port, process, WebAppUrl));
            await page.SendAsync("Log.enable");
            await page.SendAsync("Runtime.enable");
            await page.SendAsync("Page.enable");
            await page.WaitForAsync(BootCondition, 30_000);
            await WaitForViewsAsync(port, process);
            return new AppProcess(process, dataDir, ownsDataDir, port, variables, page);
        }
        catch
        {
            page?.Dispose();
            Kill(process);
            process.Dispose();
            DeleteScratchRegistry(variables["ARR411_SOFTWARE_KEY"]);
            if (ownsDataDir) TryDeleteDirectory(dataDir);
            throw;
        }
    }

    /// <summary>
    /// Runs the app a second time with the same data folder, as the system does when the user opens a link or a file while the app runs:
    /// the running instance gets the request and the new process leaves. Returns what that process returned.
    /// </summary>
    public Task<int> LaunchSecondAsync(params string[] args) => RunAsync(DataDir, _environment, args);

    /// <summary>Like <see cref="LaunchSecondAsync"/>, run from another folder: what a relative path of the command line is relative to.</summary>
    public Task<int> LaunchSecondFromAsync(string workingDirectory, params string[] args) => RunAsync(workingDirectory, _environment, args);

    /// <summary>Runs the app to the end with a command line and returns its exit code (for the commands that do not start the app: --register, --unregister, --quit).</summary>
    public static async Task<int> RunAsync(string dataDir, IReadOnlyDictionary<string, string> environment, IReadOnlyList<string> args, int timeoutMs = 30_000)
    {
        // Only the running instance owns the debug port.
        var variables = environment.Where(pair => pair.Key != "ARR411_CDP_PORT").ToDictionary(pair => pair.Key, pair => pair.Value);
        using var process = Launch(dataDir, variables, args);
        using var timeout = new CancellationTokenSource(timeoutMs);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            throw new TimeoutException($"Arr411.exe {string.Join(' ', args)} did not end.");
        }

        return process.ExitCode;
    }

    /// <summary>The window handle, once the window is on screen.</summary>
    public async Task<IntPtr> WindowAsync()
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var window = Window;
            if (Win32.IsVisible(window)) return window;
            await Task.Delay(100);
        }

        throw new TimeoutException("The app has no window on screen.");
    }

    /// <summary>Closes the window with its close button, as a user does: the app goes on in the notification area, or ends if it is set not to.</summary>
    public void CloseWindow()
    {
        var window = Window;
        if (window == IntPtr.Zero) throw new InvalidOperationException("The app has no window.");
        Win32.PressClose(window);
    }

    /// <summary>Closes the window the way the system does when it ends a session or a program asks for it (WM_CLOSE): the app ends, wherever it would live otherwise.</summary>
    public void CloseWindowBluntly()
    {
        var window = Window;
        if (window == IntPtr.Zero) throw new InvalidOperationException("The app has no window.");
        Win32.Post(window, Win32.Close);
    }

    /// <summary>
    /// Gives the keyboard to the window of the app, which fails if Windows would not. True if the app had to be put in front: that moves the windows
    /// under the cursor, and the system then selects again the entry of a menu that the cursor rests on.
    /// </summary>
    public async Task<bool> TakeKeyboardAsync()
    {
        var window = await WindowAsync();
        for (var attempt = 0; attempt < 15; attempt++)
        {
            // A menu that is open belongs to the window that has the keyboard: asking for the foreground again would be needless.
            if (Win32.HasKeyboard(window)) return false;
            if (Win32.Foreground(window)) return true;

            await Task.Delay(200);
        }

        throw new InvalidOperationException("Windows would not give the keyboard to the app, so no key was sent.");
    }

    /// <summary>
    /// Presses keys in the window, as a user does (Ctrl+Tab is Press(Control, Tab)). The keys go to whichever window has the keyboard, so they
    /// are sent only once Windows has given it to the window of the app: they never reach another program.
    /// </summary>
    public async Task PressAsync(params ushort[] keys)
    {
        await TakeKeyboardAsync();
        Win32.Chord(keys);
    }

    // The browser process of the web views leaves a moment after the app does, and takes the lock on the profile with it.
    public static Task WaitForProfileReleaseAsync(string dataDir)
    {
        var lockFile = Path.Combine(dataDir, "WebView2", "EBWebView", "lockfile");
        return Wait.UntilAsync(
            () =>
            {
                try
                {
                    using var stream = new FileStream(lockFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                    return true;
                }
                catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
                {
                    return true;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return false;
                }
            },
            "the browser process of the previous run to let go of the profile");
    }

    /// <summary>Waits for the process to end, and returns its exit code.</summary>
    public async Task<int> WaitForExitAsync(int timeoutMs = 15_000)
    {
        using var timeout = new CancellationTokenSource(timeoutMs);
        try
        {
            await _process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException("The app did not end.\n" + ReadLog());
        }

        return _process.ExitCode;
    }

    /// <summary>A picture of what the window shows. It is also saved as <paramref name="name"/>.png in the folder named by ARR411_E2E_SHOTS, if set.</summary>
    public async Task<Bitmap> CaptureAsync(string name) => Saved(await WindowCapture.CaptureAsync(await WindowAsync()), name);

    /// <summary>A picture of the title bar of the window. Saved like the one of <see cref="CaptureAsync"/>.</summary>
    public async Task<Bitmap> CaptureTitleBarAsync(string name) => Saved(await WindowCapture.CaptureTitleBarAsync(await WindowAsync()), name);

    private static Bitmap Saved(Bitmap bitmap, string name)
    {
        var folder = Environment.GetEnvironmentVariable("ARR411_E2E_SHOTS");
        if (!string.IsNullOrEmpty(folder))
        {
            Directory.CreateDirectory(folder);
            bitmap.Save(Path.Combine(folder, name + ".png"), System.Drawing.Imaging.ImageFormat.Png);
        }

        return bitmap;
    }

    /// <summary>Loads the web app again, as a crash of its view or a refresh would, and waits for it to boot.</summary>
    public async Task ReloadAsync()
    {
        await Page.EvaluateAsync("window.__beforeReload = true");
        await Page.SendAsync("Page.reload");
        await Page.WaitForAsync("!window.__beforeReload && " + BootCondition, 30_000);
    }

    /// <summary>Crashes the browser process behind the web views (the debug port has an endpoint for it), which takes all of the views with it.</summary>
    public async Task CrashBrowserAsync()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        using var version = JsonDocument.Parse(await http.GetStringAsync($"http://127.0.0.1:{_port}/json/version"));
        using var browser = await CdpClient.ConnectAsync(version.RootElement.GetProperty("webSocketDebuggerUrl").GetString()!);
        try
        {
            await browser.SendAsync("Browser.crash", timeoutMs: 5_000);
        }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException)
        {
            // The browser does not answer a command that kills it.
        }
    }

    /// <summary>
    /// Follows the app through a restart that it makes by itself (after the browser process behind its views stopped): waits for the process that
    /// takes over, which the log names, and returns it once its web app has booted. This instance is finished: the returned one is the app now,
    /// and cleans up what both used.
    /// </summary>
    public async Task<AppProcess> FollowRestartAsync(int timeoutMs = 60_000)
    {
        var processId = 0;
        try
        {
            await Wait.UntilAsync(() => (processId = LastStartedProcess()) is not 0 && processId != _process.Id, "a new process taking over", timeoutMs);
        }
        catch (TimeoutException ex)
        {
            throw new TimeoutException(ex.Message + "\n" + ReadLog());
        }

        Process process;
        try
        {
            process = Process.GetProcessById(processId);
        }
        catch (ArgumentException)
        {
            throw new InvalidOperationException("The app that took over ended at once.\n" + ReadLog());
        }

        CdpClient? page = null;
        try
        {
            page = await CdpClient.ConnectAsync(await WaitForPageAsync(_port, process, WebAppUrl));
            await page.SendAsync("Log.enable");
            await page.SendAsync("Runtime.enable");
            await page.SendAsync("Page.enable");
            await page.WaitForAsync(BootCondition, 30_000);
            await WaitForViewsAsync(_port, process);
        }
        catch
        {
            page?.Dispose();
            Kill(process);
            process.Dispose();
            throw;
        }

        CloseConnections();
        _disposed = true;
        return new AppProcess(process, DataDir, _ownsDataDir, _port, _environment, page);
    }

    // The process that wrote the last "starting" line of the log (0 if there is none, or the line is not complete yet): every run of the app says which it is.
    private int LastStartedProcess()
    {
        const string marker = " starting (process ";
        var log = ReadLog();
        var at = log.LastIndexOf(marker, StringComparison.Ordinal);
        if (at < 0) return 0;

        var start = at + marker.Length;
        var end = log.IndexOf(')', start);
        return end > start && int.TryParse(log.AsSpan(start, end - start), NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : 0;
    }

    /// <summary>
    /// The hidden engine page, connected on first use. It is what runs the background logic of the extension.
    /// After <see cref="ReloadEngineAsync"/> the same connection keeps following the page.
    /// </summary>
    /// <param name="fresh">Connect again, as after a crash of the page's view, which may have ended the connection.</param>
    public async Task<CdpClient> EngineAsync(bool fresh = false)
    {
        if (_engine is not null)
        {
            if (!fresh) return _engine;
            _engine.Dispose();
            _engine = null;
        }

        var engine = await CdpClient.ConnectAsync(await WaitForPageAsync(_port, _process, EngineUrl));
        try
        {
            await engine.SendAsync("Log.enable");
            await engine.SendAsync("Runtime.enable");
            await engine.SendAsync("Page.enable");
            await engine.WaitForAsync(EngineBootCondition, 30_000);
        }
        catch
        {
            engine.Dispose();
            throw;
        }

        return _engine = engine;
    }

    /// <summary>Loads the engine page again and waits for it to start.</summary>
    public async Task ReloadEngineAsync()
    {
        var engine = await EngineAsync();
        await engine.EvaluateAsync("window.__beforeReload = true");
        await engine.SendAsync("Page.reload");
        await engine.WaitForAsync("!window.__beforeReload && " + EngineBootCondition, 30_000);
    }

    /// <summary>
    /// The page of the C411 tab, connected on first use (the tab loads its page when it is first shown). It is the page of the fake site,
    /// with the scripts of the app in it. The connection keeps following the tab from page to page.
    /// </summary>
    /// <param name="fresh">Connect again, as after a crash of the page's view.</param>
    public async Task<CdpClient> C411Async(bool fresh = false)
    {
        if (_c411 is not null)
        {
            if (!fresh) return _c411;
            _c411.Dispose();
            _c411 = null;
        }

        var tab = await CdpClient.ConnectAsync(await WaitForPageAsync(_port, _process, C411Url));
        try
        {
            await tab.SendAsync("Log.enable");
            await tab.SendAsync("Runtime.enable");
            await tab.SendAsync("Page.enable");
            await tab.WaitForAsync("document.readyState === 'complete'", 30_000);
        }
        catch
        {
            tab.Dispose();
            throw;
        }

        return _c411 = tab;
    }

    /// <summary>How many pages the user could see or use that the browser has open (a popup would add one). The hidden engine is not one of them.</summary>
    public Task<int> PageCountAsync() => CountViewsAsync(_port);

    /// <summary>The addresses of the pages the browser has open, the hidden engine included.</summary>
    public async Task<IReadOnlyList<string>> PageUrlsAsync() => await ListPageUrlsAsync(_port);

    /// <summary>The addresses the app handed to the default browser (in the tests, written to a file instead), in order.</summary>
    public IReadOnlyList<string> OpenedLinks()
    {
        try
        {
            using var stream = new FileStream(BrowserSinkFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return [];
        }
    }

    /// <summary>The text the app last copied (in the tests, written to a file instead of the clipboard); null if it copied nothing.</summary>
    public string? CopiedText()
    {
        try
        {
            using var stream = new FileStream(ClipboardSinkFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    /// <summary>The app log, which the process keeps open.</summary>
    public string ReadLog()
    {
        if (!File.Exists(LogFile)) return "";
        using var stream = new FileStream(LogFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>The settings as the app saved them, read the way it does but without its side effects (it renames a file it cannot read). Null while the file is missing or being written.</summary>
    public JsonObject? ReadStoredConfig()
    {
        try
        {
            using var stream = new FileStream(ConfigFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var content = new MemoryStream();
            stream.CopyTo(content);
            return JsonNode.Parse(new DpapiProtector().Unprotect(content.ToArray())) as JsonObject;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Waits until the saved settings satisfy a condition, and returns them.</summary>
    public async Task<JsonObject> WaitForStoredConfigAsync(Func<JsonObject, bool> condition, string what, int timeoutMs = 10_000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            var stored = ReadStoredConfig();
            if (stored is not null && condition(stored)) return stored;
            await Task.Delay(100);
        }

        throw new TimeoutException($"The settings never held: {what}");
    }

    /// <summary>Waits until the app log says something, at least <paramref name="count"/> times.</summary>
    public async Task WaitForLogAsync(string text, int timeoutMs = 10_000, int count = 1)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (CountInLog(text) >= count) return;
            await Task.Delay(100);
        }

        throw new TimeoutException($"The log never said ({count}x): {text}\n{ReadLog()}");
    }

    /// <summary>How many times the app log says something.</summary>
    public int CountInLog(string text)
    {
        var log = ReadLog();
        var count = 0;
        for (var at = log.IndexOf(text, StringComparison.Ordinal); at >= 0; at = log.IndexOf(text, at + text.Length, StringComparison.Ordinal)) count++;
        return count;
    }

    /// <summary>Quits the app like the Quit item of its menu does, and returns the exit code.</summary>
    public async Task<int> CloseAsync()
    {
        CloseConnections();
        await QuitAsync();
        return _process.ExitCode;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        CloseConnections();
        try
        {
            await QuitAsync();
        }
        catch (TimeoutException)
        {
            // It was killed: a test that wants to know that the app quits says so with CloseAsync.
        }

        _process.Dispose();
        DeleteScratchRegistry(SoftwareKey);
        if (_ownsDataDir) await Task.Run(() => TryDeleteDirectory(DataDir));
    }

    /// <summary>Removes what a test registered under its scratch key (and the root of those keys, once the last one is gone). Keys outside the scratch root are never touched.</summary>
    public static void DeleteScratchRegistry(string key)
    {
        if (!key.StartsWith(ScratchRoot + "\\", StringComparison.Ordinal)) return;

        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(key, throwOnMissingSubKey: false);
            bool empty;
            using (var root = Registry.CurrentUser.OpenSubKey(ScratchRoot))
            {
                empty = root is { SubKeyCount: 0, ValueCount: 0 };
            }

            if (empty) Registry.CurrentUser.DeleteSubKey(ScratchRoot, throwOnMissingSubKey: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Security.SecurityException)
        {
            // Left behind: it is a scratch key, which the next run does not read.
        }
    }

    private static Process Launch(string dataDir, IReadOnlyDictionary<string, string> variables, IReadOnlyList<string> args)
    {
        var exe = ExePath;
        if (!File.Exists(exe)) throw new FileNotFoundException("Build the Debug configuration of Arr411.Desktop first.", exe);

        var start = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = dataDir };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        foreach (var (name, value) in variables) start.Environment[name] = value;
        return Process.Start(start) ?? throw new InvalidOperationException("Arr411.exe did not start.");
    }

    private void CloseConnections()
    {
        Page.Dispose();
        _engine?.Dispose();
        _c411?.Dispose();
    }

    // The window's close button no longer ends the app (it goes to the notification area): quitting is what the Quit item does, and --quit asks for it.
    private async Task QuitAsync()
    {
        if (!_process.HasExited)
        {
            try
            {
                await RunAsync(DataDir, _environment, ["--quit"], timeoutMs: 20_000);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                await _process.WaitForExitAsync(timeout.Token);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
                Kill(_process);
                throw new TimeoutException("The app did not quit.\n" + ReadLog());
            }
        }
    }

    private static string DownloadsOf(string dataDir) => Path.Combine(dataDir, "Downloads");

    private static async Task<List<string>> ListPageUrlsAsync(int port)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        using var targets = JsonDocument.Parse(await http.GetStringAsync($"http://127.0.0.1:{port}/json/list"));
        return targets.RootElement.EnumerateArray()
            .Where(target => target.GetProperty("type").GetString() == "page")
            .Select(target => target.GetProperty("url").GetString() ?? "")
            .ToList();
    }

    // The pages of the browser a user can see or use: the hidden engine is not one of them.
    private static async Task<int> CountViewsAsync(int port) =>
        (await ListPageUrlsAsync(port)).Count(url => !url.StartsWith(EngineUrl, StringComparison.Ordinal));

    // The web app boots first, then the engine and the C411 tab get their views (blank until they load something): wait for all of them,
    // so that a count of pages is stable.
    private static async Task WaitForViewsAsync(int port, Process process)
    {
        var deadline = Environment.TickCount64 + 30_000;
        while (Environment.TickCount64 < deadline)
        {
            if (process.HasExited) throw new InvalidOperationException($"Arr411.exe exited early (code {process.ExitCode}).");

            var urls = await ListPageUrlsAsync(port);
            var engineLoaded = urls.Any(url => url.StartsWith(EngineUrl, StringComparison.Ordinal));
            if (engineLoaded && urls.Count(url => !url.StartsWith(EngineUrl, StringComparison.Ordinal)) >= ViewCount) return;
            await Task.Delay(100);
        }

        throw new TimeoutException("The engine or the C411 tab never got its view.");
    }

    private static async Task<string> WaitForPageAsync(int port, Process process, string urlPrefix)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var deadline = Environment.TickCount64 + 45_000;
        while (Environment.TickCount64 < deadline)
        {
            if (process.HasExited) throw new InvalidOperationException($"Arr411.exe exited early (code {process.ExitCode}).");
            try
            {
                using var targets = JsonDocument.Parse(await http.GetStringAsync($"http://127.0.0.1:{port}/json/list"));
                foreach (var target in targets.RootElement.EnumerateArray())
                {
                    var url = target.GetProperty("url").GetString() ?? "";
                    if (target.GetProperty("type").GetString() == "page" && url.StartsWith(urlPrefix, StringComparison.Ordinal))
                    {
                        return target.GetProperty("webSocketDebuggerUrl").GetString()!;
                    }
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                // The debug port is not open yet.
            }

            await Task.Delay(200);
        }

        throw new TimeoutException($"No page at {urlPrefix} ever showed up on the debug port.");
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Already gone.
        }
    }
}
