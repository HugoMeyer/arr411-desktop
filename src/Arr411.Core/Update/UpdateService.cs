using Arr411.Core.Config;
using Arr411.Core.Logging;

namespace Arr411.Core.Update;

/// <summary>
/// Keeps the app up to date from the releases of its GitHub repository: looks for a newer one now and then, downloads it and checks what came, and hands
/// it to <see cref="UpdateApplier"/> that swaps the files once the app has left. Nothing here throws to its caller: what goes wrong is logged, and shown
/// as the <c>error</c> status.
/// </summary>
public sealed partial class UpdateService : IUpdateFeed, IDisposable
{
    internal const string Repository = "HugoMeyer/arr411-desktop";
    internal const string AssetName = "Arr411.exe";
    internal const string SumsName = "SHA256SUMS.txt";
    internal const long MaxBytes = 200L * 1024 * 1024;
    internal const int MaxNotesChars = 4000;

    private static readonly string LatestPage = $"https://github.com/{Repository}/releases/latest";

    private readonly UpdateOptions _o;
    private readonly TimeProvider _clock;
    private readonly string _folder;
    private readonly string _exeFolder;
    private readonly bool _exeIsApp;
    private readonly Lazy<UpdateHttp> _http;
    private readonly CancellationTokenSource _life = new();
    private readonly SemaphoreSlim _operation = new(1, 1);
    private readonly List<UpdateNotice> _pending = [];

    // Never held while calling out (the log, events, files, the network); _publishGate keeps the states in order, _saveGate the writes of the record.
    private readonly object _gate = new();
    private readonly object _publishGate = new();
    private readonly object _saveGate = new();

    private UpdateRecord _record = new();
    private UpdateState _state;
    private string _mode;
    private string? _busy;
    private double? _progress;
    private string? _error;
    private bool _errorKept;
    private bool _stagedReady;
    private bool _initialized;
    private bool _started;
    private int _installs;
    private string? _rejected;
    private DateTimeOffset? _lastManualCheck;
    private long? _lastProgress;
    private CancellationTokenSource? _download;
    private ITimer? _timer;
    private ITimer? _retryTimer;
    private EventHandler<ConfigChangedEventArgs>? _configHandler;
    private volatile bool _writable;
    private volatile bool _disposed;

    public UpdateService(UpdateOptions options)
    {
        _o = options;
        _clock = options.Clock;
        _folder = UpdateFiles.Folder(options.Paths);
        _exeFolder = Path.GetDirectoryName(options.ExePath) ?? "";
        _exeIsApp = IsApplicationPath(options.ExePath);
        _http = new Lazy<UpdateHttp>(() => new UpdateHttp(options.Handler ?? UpdateHttp.CreateHandler()), LazyThreadSafetyMode.ExecutionAndPublication);
        _record.App = options.CurrentVersion;
        _mode = ReadMode();
        _state = Compose();
    }

    public event EventHandler<UpdateState>? StateChanged;

    /// <summary>Something to tell the user, once (see <see cref="UpdateNotice"/>). Raised on any thread.</summary>
    public event EventHandler<UpdateNotice>? Notice;

    /// <summary>The app is to quit now: the installation of the new version is waiting for it to be gone. Raised on any thread.</summary>
    public event EventHandler? RestartRequested;

    public UpdateState State
    {
        get
        {
            lock (_gate) return _state;
        }
    }

    public void CheckNow() => Background(() => CheckAsync(manual: true));

    public void Install() => Background(InstallAsync);

    /// <summary>Reads what the last run left (the state, the error of the applier, the downloaded file) and cleans up. Call it once, before anything else.</summary>
    public void Initialize()
    {
        lock (_gate)
        {
            if (_initialized) return;

            _initialized = true;
        }

        if (!_o.Disabled)
        {
            try
            {
                InitializeCore();
            }
            catch (Exception ex)
            {
                Log.Error("Update initialisation failed", ex);
            }
        }

        Publish(force: true);
    }

    /// <summary>
    /// Starts the schedule (the first check about 30 seconds from now, then one every 6 hours) and follows the <c>autoUpdate</c> setting. Notices that
    /// were waiting (the failure of the last swap) are raised now: subscribe to <see cref="Notice"/> first.
    /// </summary>
    /// <param name="updatedFrom">The version this run was started to replace (the applier says so), to tell the user it worked.</param>
    public void Start(string? updatedFrom = null)
    {
        if (_o.Disabled || _disposed) return;

        lock (_gate)
        {
            if (_started) return;

            _started = true;
        }

        _configHandler = OnConfigChanged;
        _o.Config.Changed += _configHandler;
        lock (_gate) _mode = ReadMode();
        ApplySchedule();

        List<UpdateNotice> notices;
        lock (_gate)
        {
            if (UpdateVersion.IsNewer(_o.CurrentVersion, updatedFrom))
            {
                _pending.Add(new UpdateNotice(UpdateNoticeKind.Updated, _o.CurrentVersion, UpdateText.UpdatedTitle, UpdateText.Updated(_o.CurrentVersion)));
            }

            notices = [.. _pending];
            _pending.Clear();
        }

        foreach (var notice in notices) RaiseNotice(notice);
        Publish();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;

            _disposed = true;
        }

        if (_configHandler is not null) _o.Config.Changed -= _configHandler;

        try
        {
            _life.Cancel();
        }
        catch (AggregateException ex)
        {
            Log.Warn($"Update shutdown: {ex.GetType().Name}");
        }

        _timer?.Dispose();
        _retryTimer?.Dispose();
        if (_http.IsValueCreated) _http.Value.Dispose();
    }

    private UpdateHttp Http => _http.Value;

    private string ReadMode() => UpdateMode.From(_o.Config.GetString(UpdateMode.ConfigKey));

    private static bool IsApplicationPath(string path) =>
        Path.IsPathFullyQualified(path)
        && path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(Path.GetFileName(path), "dotnet.exe", StringComparison.OrdinalIgnoreCase);

    // The state the web app sees. Call with _gate held.
    private UpdateState Compose()
    {
        var release = _record.Release;
        var latest = release?.Version;
        var available = latest is not null && UpdateVersion.IsNewer(latest, _o.CurrentVersion);
        var ready = available && _stagedReady && _record.Staged?.Version == latest;

        string status;
        if (_busy is not null) status = _busy;
        else if (_error is not null) status = UpdateStatus.Error;
        else if (ready) status = UpdateStatus.Ready;
        else if (available || (latest is null && _record.CheckedAt is null)) status = UpdateStatus.Idle;
        else status = UpdateStatus.UpToDate;

        return new UpdateState(
            Supported: !_o.Disabled,
            Current: _o.CurrentVersion,
            Latest: latest,
            Available: available,
            Status: status,
            Progress: _busy == UpdateStatus.Downloading ? Math.Round(_progress ?? 0, 3) : null,
            Notes: release?.Notes ?? "",
            Url: string.IsNullOrEmpty(release?.PageUrl) ? LatestPage : release.PageUrl,
            CheckedAt: _record.CheckedAt,
            CanInstall: CanInstall(release),
            Error: _error,
            Mode: _mode);
    }

    // Whether this run can replace itself by the release: something to download, something to check it by, and a folder it can write to.
    // Call with _gate held.
    private bool CanInstall(CachedRelease? release) =>
        !_o.Disabled
        && _exeIsApp
        && _writable
        && release is { AssetUrl: not null, Size: > 0 and <= MaxBytes }
        && (release.Digest is not null || release.SumsUrl is not null);

    private void Publish(bool force = false)
    {
        lock (_publishGate)
        {
            UpdateState state;
            lock (_gate)
            {
                state = Compose();
                if (!force && state == _state) return;

                _state = state;
            }

            foreach (var handler in (StateChanged?.GetInvocationList() ?? []).Cast<EventHandler<UpdateState>>())
            {
                try
                {
                    handler(this, state);
                }
                catch (Exception ex)
                {
                    Log.Error("Update state handler failed", ex);
                }
            }
        }
    }

    private void Save()
    {
        lock (_saveGate)
        {
            string json;
            lock (_gate) json = _record.ToJson();
            UpdateFiles.Save(_folder, json);
        }
    }

    private void Begin(string status, bool clearError)
    {
        lock (_gate)
        {
            _busy = status;
            _progress = null;
            if (clearError)
            {
                _error = null;
                _errorKept = false;
            }
        }

        Publish();
    }

    private void End()
    {
        lock (_gate)
        {
            _busy = null;
            _progress = null;
        }

        Publish();
    }

    private void Fail(UpdateFailure failure, bool shown = true)
    {
        Log.Warn($"Update failed: {failure.Detail ?? failure.Message}");
        lock (_gate)
        {
            _busy = null;
            _progress = null;
            if (shown)
            {
                _error = failure.Message;
                _errorKept = false;
            }
        }

        Publish();
    }

    // Anything an update step can throw, as what to tell the user.
    private static UpdateFailure Classify(Exception ex) => ex switch
    {
        UpdateFailure failure => failure,
        OperationCanceledException => new UpdateFailure(UpdateText.NoAnswer, "no answer in time"),
        HttpRequestException => new UpdateFailure(UpdateText.CannotReach, ex.GetType().Name + ": " + (ex.InnerException?.GetType().Name ?? ex.Message)),
        IOException or UnauthorizedAccessException => new UpdateFailure(UpdateText.CannotSave, ex.GetType().Name + ": " + ex.Message),
        _ => new UpdateFailure(UpdateText.Unexpected, ex.GetType().Name + ": " + ex.Message),
    };

    private void Background(Func<Task> work) => _ = Task.Run(async () =>
    {
        try
        {
            await work().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error("Update task failed", ex);
        }
    });

    private void RaiseNotice(UpdateNotice notice)
    {
        try
        {
            Notice?.Invoke(this, notice);
        }
        catch (Exception ex)
        {
            Log.Error("Update notice handler failed", ex);
        }
    }

    // A notice a version gets once: what was told is kept with the state.
    private void NotifyOnce(UpdateNoticeKind kind, string version, string title, string text)
    {
        var key = $"{kind}:{version}";
        lock (_gate)
        {
            if (_record.Notified == key) return;

            _record.Notified = key;
        }

        Save();
        RaiseNotice(new UpdateNotice(kind, version, title, text));
    }

    private void OnConfigChanged(object? sender, ConfigChangedEventArgs e)
    {
        try
        {
            var mode = ReadMode();
            string previous;
            lock (_gate)
            {
                previous = _mode;
                _mode = mode;
            }

            if (mode == previous) return;

            Log.Info($"Update mode: {mode}");
            ApplySchedule();
            if (mode == UpdateMode.Auto) Background(DownloadWhenAutoAsync);
            else CancelDownloadNobodyAskedFor();

            Publish();
        }
        catch (Exception ex)
        {
            Log.Error("Update mode change failed", ex);
        }
    }

    // The timer runs unless updates are off: the manual check does not need it.
    private void ApplySchedule()
    {
        lock (_gate)
        {
            if (_disposed) return;

            if (_mode == UpdateMode.Off)
            {
                _timer?.Dispose();
                _timer = null;
            }
            else
            {
                _timer ??= _clock.CreateTimer(_ => Background(() => CheckAsync(manual: false)), null, _o.FirstCheckDelay, _o.CheckInterval);
            }
        }
    }

    private void CancelDownloadNobodyAskedFor()
    {
        CancellationTokenSource? running;
        lock (_gate) running = _installs > 0 ? null : _download;

        try
        {
            running?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // It just ended.
        }
    }
}
