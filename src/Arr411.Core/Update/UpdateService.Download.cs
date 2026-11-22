using System.Net;
using System.Security.Cryptography;
using System.Text;
using Arr411.Core.Logging;

namespace Arr411.Core.Update;

public sealed partial class UpdateService
{
    private const int BufferBytes = 64 * 1024;
    private const int MoveAttempts = 3;
    private const string PartSuffix = ".part";

    private string StagedPath(StagedUpdate staged) => Path.Combine(_folder, staged.File);

    /// <summary>
    /// Downloads a release next to the state and keeps the file only when it is what the release says it is: the size, and every hash the release
    /// carries. True when the verified file is staged. Call it holding <see cref="_operation"/>.
    /// </summary>
    /// <param name="requested">The user asked for it (Install): a failure is shown, and it is tried even when this version was refused earlier.</param>
    /// <param name="notify">Tell the user when it is ready (the automatic mode, nobody is looking).</param>
    private async Task<bool> DownloadAsync(CachedRelease release, bool requested, bool notify)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_life.Token);
        lock (_gate)
        {
            _download = linked;
            _lastProgress = null;
        }

        var token = linked.Token;
        var part = Path.Combine(_folder, UpdateFiles.StagedName(release.Version) + PartSuffix);
        Begin(UpdateStatus.Downloading, clearError: requested);
        try
        {
            var expected = await ExpectedHashAsync(release, token).ConfigureAwait(false);
            await FetchAsync(release, part, expected, token).ConfigureAwait(false);
            await StageAsync(release, part, expected, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Told to stop (the setting changed, the app is leaving): nothing went wrong.
            if (!_disposed) End();
            return false;
        }
        catch (Exception ex)
        {
            if (_disposed) return false;

            var failure = Classify(ex);
            if (failure.Final)
            {
                lock (_gate) _rejected = release.Version;
            }

            Fail(failure);
            return false;
        }
        finally
        {
            lock (_gate) _download = null;
            UpdateFiles.TryDelete(part);
        }

        End();
        if (notify) NotifyOnce(UpdateNoticeKind.Ready, release.Version, UpdateText.ReadyTitle, UpdateText.Ready(release.Version));

        return true;
    }

    // The hash the file has to have: the digest of GitHub, the line of SHA256SUMS.txt, or both when the release has both (they must agree).
    private async Task<string> ExpectedHashAsync(CachedRelease release, CancellationToken token)
    {
        string? sums = null;
        if (release.SumsUrl is { } sumsUrl)
        {
            sums = await FetchSumsAsync(sumsUrl, token).ConfigureAwait(false)
                ?? throw new UpdateFailure(UpdateText.SumsUnusable, "no usable line for the program in the sums file", final: true);
        }

        if (release.Digest is not null && sums is not null && release.Digest != sums)
        {
            throw new UpdateFailure(UpdateText.WrongHash, "the digest and the sums file of the release disagree", final: true);
        }

        return release.Digest ?? sums ?? throw new UpdateFailure(UpdateText.NothingToVerify, "no hash source", final: true);
    }

    private async Task<string?> FetchSumsAsync(string url, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(_o.ApiTimeout);

        using var response = await Http.GetAsync(new Uri(url), PrepareDownload, timeout.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw AssetFailure(response.StatusCode);

        var body = await UpdateHttp.ReadAsync(response, UpdateHashes.MaxSumsBytes, timeout.Token).ConfigureAwait(false);
        return UpdateHashes.FindInSums(Encoding.UTF8.GetString(body), AssetName);
    }

    // Streams the asset into the partial file, hashing as it goes. Nothing past the announced size is ever written.
    private async Task FetchAsync(CachedRelease release, string part, string expected, CancellationToken token)
    {
        Directory.CreateDirectory(_folder);

        // Armed again after every chunk: a download is given up when nothing came for StallTimeout, however long it takes otherwise.
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(token);
        stall.CancelAfter(_o.StallTimeout);
        try
        {
            using var response = await Http.GetAsync(new Uri(release.AssetUrl!), PrepareDownload, stall.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw AssetFailure(response.StatusCode);

            if (response.Content.Headers.ContentLength is { } announced && announced != release.Size)
            {
                throw new UpdateFailure(UpdateText.WrongSize, $"announced {announced} bytes, the release says {release.Size}", final: true);
            }

            await using var source = await response.Content.ReadAsStreamAsync(stall.Token).ConfigureAwait(false);
            await using var file = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, BufferBytes, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[BufferBytes];
            long total = 0;
            while (true)
            {
                stall.CancelAfter(_o.StallTimeout);
                var read = await ReadChunkAsync(source, buffer, stall.Token).ConfigureAwait(false);
                if (read == 0) break;

                if (total + read > release.Size) throw new UpdateFailure(UpdateText.WrongSize, "more bytes than the release says", final: true);

                hash.AppendData(buffer, 0, read);
                await file.WriteAsync(buffer.AsMemory(0, read), stall.Token).ConfigureAwait(false);
                total += read;
                ReportProgress(total, release.Size);
            }

            if (total != release.Size) throw new UpdateFailure(UpdateText.WrongSize, $"received {total} bytes, the release says {release.Size}", final: true);

            if (Convert.ToHexStringLower(hash.GetHashAndReset()) != expected) throw new UpdateFailure(UpdateText.WrongHash, "the hash of the download is not the expected one", final: true);

            await file.FlushAsync(stall.Token).ConfigureAwait(false);
            file.Flush(flushToDisk: true);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new UpdateFailure(UpdateText.Stalled, "nothing received in time");
        }
    }

    private static async Task<int> ReadChunkAsync(Stream source, byte[] buffer, CancellationToken token)
    {
        try
        {
            return await source.ReadAsync(buffer, token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException)
        {
            throw new UpdateFailure(UpdateText.DownloadFailed, $"connection lost: {ex.GetType().Name}");
        }
    }

    private void ReportProgress(long done, long size)
    {
        var now = _clock.GetTimestamp();
        if (_lastProgress is { } last && done < size && _clock.GetElapsedTime(last, now) < _o.ProgressInterval) return;

        _lastProgress = now;
        lock (_gate) _progress = (double)done / size;
        Publish();
    }

    // The verified file takes its name; the others (an older version, a partial file) are no longer wanted.
    private async Task StageAsync(CachedRelease release, string part, string expected, CancellationToken token)
    {
        var name = UpdateFiles.StagedName(release.Version);
        var target = Path.Combine(_folder, name);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(part, target, overwrite: true);
                break;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < MoveAttempts)
            {
                // A virus scanner may hold the new file for a moment.
                await Task.Delay(TimeSpan.FromMilliseconds(300), token).ConfigureAwait(false);
            }
        }

        DeleteStagedExcept(name);
        lock (_gate)
        {
            _record.Staged = new StagedUpdate(release.Version, name, expected, release.Size);
            _stagedReady = true;
            _error = null;
            _errorKept = false;
        }

        Save();
        Log.Info($"Update {release.Version} downloaded and verified");
    }

    private void DeleteStagedExcept(string keep)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(_folder, "Arr411-*"))
            {
                if (!Path.GetFileName(file).Equals(keep, StringComparison.OrdinalIgnoreCase)) UpdateFiles.TryDelete(file);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Update folder not cleaned: {ex.GetType().Name}");
        }
    }

    private static UpdateFailure AssetFailure(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests => new UpdateFailure(UpdateText.RateLimited, $"HTTP {(int)status}"),
        _ => new UpdateFailure(UpdateText.DownloadFailed, $"HTTP {(int)status}"),
    };
}
