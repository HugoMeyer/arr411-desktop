using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using Arr411.Core.Logging;

namespace Arr411.Core.Update;

public sealed partial class UpdateService
{
    /// <summary>
    /// A start with nothing to do for the user (no torrent, no link, no tab): when an update is downloaded and checked, hands it over before any window
    /// exists and says so. The process is then to end at once; the applier starts the new version. False when nothing was done.
    /// </summary>
    /// <param name="hasIntake">The start carries something to open.</param>
    /// <param name="background">The start was a quiet one (autostart): the new version starts quiet too.</param>
    public bool TryApplyAtStart(bool hasIntake, bool background)
    {
        try
        {
            return !hasIntake && MayApplyUnattended() && LaunchApply(restart: true, background);
        }
        catch (Exception ex)
        {
            Log.Error("Update at start failed", ex);
            return false;
        }
    }

    /// <summary>
    /// The user is quitting the app: when an update is downloaded and checked, hands it over, to be installed once this process has gone. The app is
    /// not started again. Never for a shutdown of Windows, a crash or a lost browser: only the host decides to call it. False when nothing was done.
    /// </summary>
    public bool TryApplyAtExit()
    {
        try
        {
            return MayApplyUnattended() && LaunchApply(restart: false, background: false);
        }
        catch (Exception ex)
        {
            Log.Error("Update at exit failed", ex);
            return false;
        }
    }

    /// <summary>The installation of the user's own request (<see cref="IUpdateFeed.Install"/>): downloads when needed, then asks the host to quit.</summary>
    internal async Task InstallAsync()
    {
        if (_o.Disabled || _disposed) return;

        Interlocked.Increment(ref _installs);
        try
        {
            await _operation.WaitAsync(_life.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Interlocked.Decrement(ref _installs);
            return;
        }

        var launched = false;
        try
        {
            launched = await EnsureStagedAsync().ConfigureAwait(false) && LaunchApply(restart: true, background: false);
            if (launched) Begin(UpdateStatus.Installing, clearError: false);
        }
        catch (Exception ex)
        {
            if (!_disposed) Fail(Classify(ex));
        }
        finally
        {
            Interlocked.Decrement(ref _installs);
            _operation.Release();
        }

        if (launched) RaiseRestartRequested();
    }

    // Whether there is something verified to install, downloading it first when needed.
    private async Task<bool> EnsureStagedAsync()
    {
        CachedRelease? release;
        lock (_gate)
        {
            if (_record.Applying is not null) return false;

            release = _record.Release;
        }

        if (release is null || !UpdateVersion.IsNewer(release.Version, _o.CurrentVersion)) return false;

        RefreshWritable();
        bool installable, staged;
        lock (_gate)
        {
            installable = CanInstall(release);
            staged = _stagedReady && _record.Staged?.Version == release.Version;
        }

        if (!installable)
        {
            Fail(new UpdateFailure(UpdateText.CannotInstallHere, "this run cannot replace itself"));
            return false;
        }

        return staged || await DownloadAsync(release, requested: true, notify: false).ConfigureAwait(false);
    }

    // What an unattended apply needs: the mode, a verified file, no swap that already failed for it, a folder that can be written to right now.
    private bool MayApplyUnattended()
    {
        lock (_gate)
        {
            if (_o.Disabled || !_o.AutoApply || !_initialized || _mode != UpdateMode.Auto || !_stagedReady || !_exeIsApp) return false;

            if (_record.Staged is not { } staged || _record.Failed == staged.Version || _record.Applying is not null) return false;
        }

        return CanWriteHere();
    }

    private bool CanWriteHere()
    {
        try
        {
            return _o.CanWriteFolder(_exeFolder);
        }
        catch (Exception ex)
        {
            Log.Warn($"Update folder probe failed: {ex.GetType().Name}");
            return false;
        }
    }

    /// <summary>
    /// Starts the staged file as the applier, after checking it is still the file that was downloaded. It waits for this process to end, swaps the
    /// files, and (<paramref name="restart"/>) starts the new version. True when it was started.
    /// </summary>
    private bool LaunchApply(bool restart, bool background)
    {
        StagedUpdate? staged;
        lock (_gate)
        {
            staged = _record.Staged;
            if (staged is null || !_stagedReady || _record.Applying is not null) return false;
        }

        var path = StagedPath(staged);
        if (!StagedIsIntact(staged, path))
        {
            lock (_gate)
            {
                _record.Staged = null;
                _stagedReady = false;
            }

            UpdateFiles.TryDelete(path);
            Save();
            Fail(new UpdateFailure(UpdateText.StagedChanged, "the staged file is not the one that was downloaded"));
            return false;
        }

        lock (_gate) _record.Applying = new ApplyAttempt(staged.Version, _o.ExePath);
        Save();

        var info = new ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = _folder };
        info.ArgumentList.Add(UpdateApplier.Flag);
        info.ArgumentList.Add("--target");
        info.ArgumentList.Add(_o.ExePath);
        info.ArgumentList.Add("--wait-pid");
        info.ArgumentList.Add(_o.ProcessId.ToString(CultureInfo.InvariantCulture));
        info.ArgumentList.Add("--from");
        info.ArgumentList.Add(_o.CurrentVersion);
        if (restart)
        {
            info.ArgumentList.Add("--restart");
            if (background) info.ArgumentList.Add("--background");
        }

        var started = false;
        try
        {
            started = _o.StartProcess(info);
        }
        catch (Exception ex)
        {
            Log.Error("Update applier not started", ex);
        }

        if (!started)
        {
            lock (_gate) _record.Applying = null;
            Save();
            Fail(new UpdateFailure(UpdateText.LaunchFailed, "the applier did not start"));
            return false;
        }

        Log.Info($"Update {staged.Version} handed over (restart: {restart})");
        return true;
    }

    // The file has to be the one that was verified, down to the last byte: it is about to be given the place of the program.
    private static bool StagedIsIntact(StagedUpdate staged, string path)
    {
        try
        {
            if (new FileInfo(path).Length != staged.Size) return false;

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferBytes, FileOptions.SequentialScan);
            return Convert.ToHexStringLower(SHA256.HashData(stream)) == staged.Sha256;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void RaiseRestartRequested()
    {
        try
        {
            RestartRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Log.Error("Update restart handler failed", ex);
        }
    }
}
