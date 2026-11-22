using System.Text;
using Arr411.Core.Config;
using Arr411.Core.Logging;

namespace Arr411.Core.Update;

/// <summary>The files of the updater, in the <c>updates</c> folder of the app's data.</summary>
internal static class UpdateFiles
{
    public const string FolderName = "updates";
    public const string StateName = "state.json";
    public const string ErrorName = "last-error.txt";

    private static readonly UTF8Encoding Utf8 = new(false);

    public static string Folder(AppPaths paths) => Path.Combine(paths.Root, FolderName);

    public static string StateFile(string folder) => Path.Combine(folder, StateName);

    public static string ErrorFile(string folder) => Path.Combine(folder, ErrorName);

    /// <summary>The name a verified download is kept under.</summary>
    public static string StagedName(string version) => $"Arr411-{version}.exe";

    public static UpdateRecord Load(string folder)
    {
        try
        {
            var file = StateFile(folder);
            return File.Exists(file) ? UpdateRecord.Parse(File.ReadAllText(file, Utf8)) : new UpdateRecord();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Update state not read: {ex.GetType().Name}");
            return new UpdateRecord();
        }
    }

    public static void Save(string folder, string json)
    {
        try
        {
            AtomicFile.Write(StateFile(folder), Utf8.GetBytes(json), keepBackup: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Update state not saved: {ex.GetType().Name}");
        }
    }

    /// <summary>What the applier leaves for the next start when it could not do its job: the version it was about, then the message, in French.</summary>
    public static void WriteError(string folder, string? version, string message)
    {
        try
        {
            AtomicFile.Write(ErrorFile(folder), Utf8.GetBytes((version ?? "") + "\n" + message + "\n"), keepBackup: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Update error not written: {ex.GetType().Name}");
        }
    }

    /// <summary>The error left by the applier, removed once read; null when there is none.</summary>
    public static (string? Version, string Message)? TakeError(string folder)
    {
        var file = ErrorFile(folder);
        try
        {
            if (!File.Exists(file)) return null;

            var text = File.ReadAllText(file, Utf8);
            File.Delete(file);
            var lines = text.Split('\n', 2);
            var version = lines.Length > 1 ? UpdateVersion.Clean(lines[0]) : null;
            return (version, UpdateText.CleanMessage(version is null ? text : lines[1]));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Deletes a file that may be in use or protected for a moment; false when it is still there.</summary>
    public static bool TryDelete(string file)
    {
        try
        {
            File.Delete(file);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return !File.Exists(file);
        }
    }
}
