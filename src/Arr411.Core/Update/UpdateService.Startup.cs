using Arr411.Core.Logging;

namespace Arr411.Core.Update;

public sealed partial class UpdateService
{
    private static readonly TimeSpan ProbeLifetime = TimeSpan.FromMinutes(10);

    // What the last run left behind: the state, what the applier says of its swap, the downloaded file. Whatever is stale goes.
    private void InitializeCore()
    {
        var current = _o.CurrentVersion;
        var record = UpdateFiles.Load(_folder);
        var notices = new List<UpdateNotice>();

        if (record.App != current)
        {
            // What was learned of the releases was learned by another version.
            record.App = current;
            record.Release = null;
            record.Etag = null;
            record.CheckedAt = null;
            record.Notified = null;
        }

        if (record.Failed is { } failed && !UpdateVersion.IsNewer(failed, current)) record.Failed = null;

        var error = ReadSwapError(record, notices);
        record.Applying = null;
        var staged = KeepStaged(record);

        lock (_gate)
        {
            _record = record;
            _error = error;
            _errorKept = error is not null;
            _stagedReady = staged;
            _pending.AddRange(notices);
        }

        if (record.Release is not null) RefreshWritable();

        CleanUp(final: false);
        Save();
    }

    // What the last applier says: the message it left, or, when it left none, that this is still the version it was to replace.
    private string? ReadSwapError(UpdateRecord record, List<UpdateNotice> notices)
    {
        var left = UpdateFiles.TakeError(_folder);
        if (left is null && record.Applying is null) return null;

        var version = left?.Version ?? record.Applying?.Version;
        if (version is not null && !UpdateVersion.IsNewer(version, _o.CurrentVersion)) return null;

        if (version is not null) record.Failed = version;

        var text = left?.Message;
        var message = text is { Length: > 0 } ? text : version is not null ? UpdateText.ApplyFailed(version) : UpdateText.Unexpected;
        Log.Warn($"Update {version ?? "?"} was not installed: {message}");
        notices.Add(new UpdateNotice(UpdateNoticeKind.Failed, version ?? _o.CurrentVersion, UpdateText.FailedTitle, message));
        return message;
    }

    // The downloaded file, when it is still wanted and still the size it was.
    private bool KeepStaged(UpdateRecord record)
    {
        if (record.Staged is not { } staged) return false;

        var path = StagedPath(staged);
        if (UpdateVersion.IsNewer(staged.Version, _o.CurrentVersion) && SizeOf(path) == staged.Size) return true;

        UpdateFiles.TryDelete(path);
        record.Staged = null;
        return false;
    }

    private static long SizeOf(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : -1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return -1;
        }
    }

    // Deletes what a swap leaves behind: the previous program (.old), a half-made copy (.new), the files of the updates folder that are not the one
    // that is waited for, the probes of a crash. A file still in use (the applier is just ending, a virus scanner is looking) is tried once more later.
    private void CleanUp(bool final)
    {
        string? keep;
        lock (_gate) keep = _record.Staged?.File;

        var busy = false;
        try
        {
            if (_exeIsApp)
            {
                busy |= !UpdateFiles.TryDelete(_o.ExePath + ".old");
                busy |= !UpdateFiles.TryDelete(_o.ExePath + ".new");
                DeleteOldProbes();
            }

            if (Directory.Exists(_folder))
            {
                foreach (var file in Directory.EnumerateFiles(_folder, "Arr411-*"))
                {
                    if (!Path.GetFileName(file).Equals(keep, StringComparison.OrdinalIgnoreCase)) busy |= !UpdateFiles.TryDelete(file);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Update clean-up failed: {ex.GetType().Name}");
        }

        if (!busy || final) return;

        lock (_gate)
        {
            if (!_disposed && _retryTimer is null)
            {
                _retryTimer = _clock.CreateTimer(_ => RetryCleanUp(), null, _o.CleanUpRetry, Timeout.InfiniteTimeSpan);
            }
        }
    }

    private void RetryCleanUp()
    {
        try
        {
            if (!_disposed) CleanUp(final: true);
        }
        catch (Exception ex)
        {
            Log.Error("Update clean-up failed", ex);
        }
    }

    private void DeleteOldProbes()
    {
        var limit = _clock.GetUtcNow().UtcDateTime - ProbeLifetime;
        foreach (var file in Directory.EnumerateFiles(_exeFolder, UpdateProbe.Prefix + "*"))
        {
            if (File.GetLastWriteTimeUtc(file) < limit) UpdateFiles.TryDelete(file);
        }
    }
}
