using System.Net;
using System.Text;
using Arr411.Core.Logging;

namespace Arr411.Core.Update;

public sealed partial class UpdateService
{
    private const int MaxApiBytes = 2 * 1024 * 1024;
    private const string ApiVersion = "2022-11-28";

    private static readonly Uri LatestApi = new($"https://api.github.com/repos/{Repository}/releases/latest");

    private string UserAgent => $"Arr411-Desktop/{_o.CurrentVersion}";

    /// <summary>
    /// Looks for a newer release now and, when the mode is auto and there is one, downloads it. A check the user asks for (<paramref name="manual"/>) is
    /// taken into account at most once a minute. Never throws: a failure is logged and becomes the <c>error</c> status.
    /// </summary>
    internal async Task CheckAsync(bool manual)
    {
        if (_o.Disabled || _disposed) return;

        if (manual && TooSoon())
        {
            Publish(force: true);
            return;
        }

        if (!_operation.Wait(0))
        {
            if (manual) Publish(force: true);

            return;
        }

        try
        {
            await CheckCoreAsync(manual).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (!_disposed) Fail(Classify(ex), shown: manual);
        }
        finally
        {
            _operation.Release();
        }
    }

    private bool TooSoon()
    {
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            if (_lastManualCheck is { } last && now >= last && now - last < _o.ManualCooldown) return true;

            _lastManualCheck = now;
            return false;
        }
    }

    private async Task CheckCoreAsync(bool manual)
    {
        Begin(UpdateStatus.Checking, clearError: manual);
        try
        {
            await QueryLatestAsync().ConfigureAwait(false);
            await AfterCheckAsync(manual).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (_disposed) return;

            // A check that fails while an update is already downloaded changes nothing for the user: it goes to the log.
            bool covered;
            lock (_gate) covered = _stagedReady;
            Fail(Classify(ex), shown: manual || !covered);
            return;
        }

        End();
    }

    private async Task QueryLatestAsync()
    {
        string? etag;
        CachedRelease? cached;
        lock (_gate)
        {
            cached = _record.Release;
            etag = cached is null ? null : _record.Etag;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_life.Token);
        timeout.CancelAfter(_o.ApiTimeout);

        using var response = await Http.GetAsync(LatestApi, request => PrepareApi(request, etag), timeout.Token).ConfigureAwait(false);
        var now = _clock.GetUtcNow().ToUnixTimeMilliseconds();

        if (response.StatusCode == HttpStatusCode.NotModified)
        {
            if (cached is null) throw new UpdateFailure(UpdateText.UnreadableAnswer, "answer 304 with nothing cached");

            lock (_gate) _record.CheckedAt = now;
            Log.Info($"Update check: {cached.Version} is still the latest release (running {_o.CurrentVersion})");
            return;
        }

        if (!response.IsSuccessStatusCode) throw StatusFailure(response.StatusCode);

        var body = await UpdateHttp.ReadAsync(response, MaxApiBytes, timeout.Token).ConfigureAwait(false);
        var release = ReleaseParser.Parse(Encoding.UTF8.GetString(body));
        var etagOfAnswer = response.Headers.ETag?.ToString();
        lock (_gate)
        {
            _record.Release = release;
            _record.Etag = release is null ? null : etagOfAnswer;
            _record.CheckedAt = now;
        }

        Log.Info($"Update check: latest release {release?.Version ?? "none"}, running {_o.CurrentVersion}");
    }

    private void PrepareApi(HttpRequestMessage request, string? etag)
    {
        request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", ApiVersion);
        if (etag is not null) request.Headers.TryAddWithoutValidation("If-None-Match", etag);
    }

    private void PrepareDownload(HttpRequestMessage request)
    {
        request.Headers.TryAddWithoutValidation("Accept", "application/octet-stream");
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
    }

    private static UpdateFailure StatusFailure(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests => new UpdateFailure(UpdateText.RateLimited, $"HTTP {(int)status}"),
        HttpStatusCode.NotFound => new UpdateFailure(UpdateText.NoRelease, "HTTP 404"),
        _ => new UpdateFailure(UpdateText.HttpError((int)status), $"HTTP {(int)status}"),
    };

    // What follows a check that worked: forget a downloaded file that is no longer the newest, and either fetch the release or tell the user about it.
    private async Task AfterCheckAsync(bool manual)
    {
        CachedRelease? release;
        lock (_gate) release = _record.Release;

        DropStaleStaged(release);
        RefreshWritable();

        bool newer, needsUser;
        lock (_gate)
        {
            newer = release is not null && UpdateVersion.IsNewer(release.Version, _o.CurrentVersion);
            needsUser = newer && !_stagedReady && (_mode == UpdateMode.Notify || (_mode == UpdateMode.Auto && !CanInstall(release)));
            if (!_errorKept) _error = null;
        }

        Save();
        if (!newer) return;

        if (ReleaseToDownload() is { } wanted)
        {
            await DownloadAsync(wanted, requested: false, notify: !manual).ConfigureAwait(false);
        }
        else if (needsUser && !manual)
        {
            NotifyOnce(UpdateNoticeKind.Available, release!.Version, UpdateText.AvailableTitle, UpdateText.Available(release.Version));
        }
    }

    // The release the automatic mode is to download now, if any.
    private CachedRelease? ReleaseToDownload()
    {
        lock (_gate)
        {
            var release = _record.Release;
            return _mode == UpdateMode.Auto
                && !_stagedReady
                && release is not null
                && UpdateVersion.IsNewer(release.Version, _o.CurrentVersion)
                && _rejected != release.Version
                && CanInstall(release)
                    ? release
                    : null;
        }
    }

    private void DropStaleStaged(CachedRelease? release)
    {
        StagedUpdate? stale = null;
        lock (_gate)
        {
            if (_record.Staged is { } staged
                && (release is null || release.Version != staged.Version || !UpdateVersion.IsNewer(release.Version, _o.CurrentVersion)))
            {
                stale = staged;
                _record.Staged = null;
                _stagedReady = false;
            }
        }

        if (stale is not null) UpdateFiles.TryDelete(StagedPath(stale));
    }

    private void RefreshWritable()
    {
        if (_o.Disabled || !_exeIsApp) return;

        try
        {
            _writable = _o.CanWriteFolder(_exeFolder);
        }
        catch (Exception ex)
        {
            Log.Warn($"Update folder probe failed: {ex.GetType().Name}");
            _writable = false;
        }
    }

    private async Task DownloadWhenAutoAsync()
    {
        if (_o.Disabled || _disposed || !_operation.Wait(0)) return;

        try
        {
            RefreshWritable();
            if (ReleaseToDownload() is { } wanted) await DownloadAsync(wanted, requested: false, notify: false).ConfigureAwait(false);
        }
        finally
        {
            _operation.Release();
        }
    }
}
