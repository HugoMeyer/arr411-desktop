using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using Arr411.Core.Config;
using Arr411.Core.Logging;

namespace Arr411.Core.Update;

/// <summary>What the applier works with; the tests give it a folder of their own, a process that is already gone, and no wait between two attempts.</summary>
internal sealed class ApplyEnvironment
{
    /// <summary>The file that runs: the downloaded program, which is about to take the place of the other one.</summary>
    public required string SelfPath { get; init; }

    public required string UpdatesFolder { get; init; }

    /// <summary>The version of the program that runs, which is the one that is installed.</summary>
    public required string Version { get; init; }

    /// <summary>Waits for a process to end; false when it is still there after the time given. A process that does not exist any more has ended.</summary>
    public Func<int, TimeSpan, bool> WaitForExit { get; init; } = WaitForProcess;

    public Action<TimeSpan> Delay { get; init; } = Thread.Sleep;

    public Func<ProcessStartInfo, bool> Start { get; init; } = UpdateOptions.StartDetached;

    public TimeSpan WaitLimit { get; init; } = TimeSpan.FromSeconds(60);

    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(1);

    public int Attempts { get; init; } = 3;

    private static bool WaitForProcess(int id, TimeSpan limit)
    {
        try
        {
            using var process = Process.GetProcessById(id);
            return process.WaitForExit((int)Math.Min(limit.TotalMilliseconds, int.MaxValue));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return true;
        }
    }
}

/// <summary>
/// The other half of an update. The downloaded program is started with <c>--apply-update</c>: it waits for the app that started it to end, copies itself
/// over that app's file, and starts it again when it is asked to. It is the first thing the program does, with no window and no other-instance check.
/// A swap that does not work leaves the program that was there as it was, and tells the next start why (<c>last-error.txt</c>).
/// </summary>
public static class UpdateApplier
{
    public const string Flag = "--apply-update";

    private const int Installed = 0;
    private const int NotInstalled = 1;
    private const int Refused = 2;

    /// <summary>The exit code of the process when it was started as the applier; null when it was not and the program starts as usual.</summary>
    public static int? TryRun(IReadOnlyList<string> args)
    {
        if (args.Count == 0 || args[0] != Flag) return null;

        try
        {
            var paths = AppPaths.FromEnvironment();
            Log.Initialize(paths.LogFile);
            return Run(args, new ApplyEnvironment
            {
                SelfPath = Environment.ProcessPath ?? "",
                UpdatesFolder = UpdateFiles.Folder(paths),
                Version = typeof(UpdateApplier).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
            });
        }
        catch (Exception ex)
        {
            Log.Error("The update could not be applied", ex);
            return NotInstalled;
        }
    }

    internal static int Run(IReadOnlyList<string> args, ApplyEnvironment env)
    {
        var request = ApplyRequest.Parse(args);
        var record = UpdateFiles.Load(env.UpdatesFolder);
        var target = "";
        var refusal = request.Problem ?? request.CheckTarget(env.SelfPath, record.Applying?.Target, out target);
        if (refusal is not null)
        {
            Log.Warn($"Update refused: {refusal}");
            UpdateFiles.WriteError(env.UpdatesFolder, env.Version, UpdateText.BadApplyArguments);
            return Refused;
        }

        if (!AppEnded(env, request.WaitPid!.Value))
        {
            Log.Warn("Update not installed: the app did not end");
            UpdateFiles.WriteError(env.UpdatesFolder, env.Version, UpdateText.NotClosed);
            return NotInstalled;
        }

        if (!IsStagedFile(env.SelfPath, record.Staged))
        {
            return Abandon(env, request, target, UpdateText.StagedChanged);
        }

        var failure = Swap(env, target);
        if (failure is not null)
        {
            Log.Warn($"Update {env.Version} not installed: {failure}");
            return Abandon(env, request, target, UpdateText.ApplyFailed(env.Version));
        }

        Log.Info($"Update {env.Version} installed");
        if (request.Restart) Launch(env, target, request.From, request.Background);

        return Installed;
    }

    private static bool AppEnded(ApplyEnvironment env, int id)
    {
        try
        {
            return env.WaitForExit(id, env.WaitLimit);
        }
        catch (Exception ex) when (ex is Win32Exception or NotSupportedException)
        {
            // A process this one may not wait for is not one it may replace the file of.
            Log.Warn($"The app could not be waited for: {ex.GetType().Name}");
            return false;
        }
    }

    // The file that runs has to be the one that was downloaded and checked, when the app says which one that was.
    private static bool IsStagedFile(string self, StagedUpdate? staged)
    {
        if (staged is null || !string.Equals(staged.File, Path.GetFileName(self), StringComparison.OrdinalIgnoreCase)) return true;

        try
        {
            using var stream = new FileStream(self, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan);
            return stream.Length == staged.Size && Convert.ToHexStringLower(SHA256.HashData(stream)) == staged.Sha256;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // The old program is kept next to the new one (.old) until the next start: a copy is made next to it first, and takes its place in one step.
    private static string? Swap(ApplyEnvironment env, string target)
    {
        var copy = target + ".new";
        var backup = target + ".old";
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Copy(env.SelfPath, copy, overwrite: true);
                if (File.Exists(target))
                {
                    EnsureNotInUse(target);
                    UpdateFiles.TryDelete(backup);
                    File.Replace(copy, target, backup, ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(copy, target);
                }

                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                PutBack(target, backup);
                UpdateFiles.TryDelete(copy);
                if (attempt >= env.Attempts) return ex.GetType().Name;

                env.Delay(env.RetryDelay);
            }
        }
    }

    // A program that runs, or a file that is read-only, cannot be opened for writing: that is the answer, before anything is moved.
    private static void EnsureNotInUse(string target)
    {
        using var stream = new FileStream(target, FileMode.Open, FileAccess.Write, FileShare.None);
    }

    // A swap that stopped half way may have set the old program aside without the new one taking its place.
    private static void PutBack(string target, string backup)
    {
        try
        {
            if (!File.Exists(target) && File.Exists(backup)) File.Move(backup, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("The program that was replaced could not be put back", ex);
        }
    }

    // What the user asked for was an update that restarts the app: whatever the outcome, the app that was there comes back.
    private static int Abandon(ApplyEnvironment env, ApplyRequest request, string target, string message)
    {
        UpdateFiles.WriteError(env.UpdatesFolder, env.Version, message);
        if (request.Restart && File.Exists(target)) Launch(env, target, null, request.Background);

        return NotInstalled;
    }

    private static void Launch(ApplyEnvironment env, string target, string? from, bool background)
    {
        var info = new ProcessStartInfo(target) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(target) ?? "" };
        if (from is not null)
        {
            info.ArgumentList.Add("--updated-from");
            info.ArgumentList.Add(from);
        }

        if (background) info.ArgumentList.Add("--background");

        try
        {
            if (!env.Start(info)) Log.Warn("The app was not started again");
        }
        catch (Exception ex)
        {
            Log.Error("The app could not be started again", ex);
        }
    }
}
