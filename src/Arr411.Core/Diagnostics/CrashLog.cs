using System.Globalization;
using System.Text;
using Arr411.Core.Logging;

namespace Arr411.Core.Diagnostics;

/// <summary>
/// crash.log: the whole story of every exception that nobody caught (or that stopped something important), with the state of the memory at that moment.
/// The normal log only keeps the names of the methods; here the full text of the exception is kept, with its inner exceptions.
/// </summary>
public static class CrashLog
{
    internal const long MaxBytes = 512 * 1024;
    internal const int MaxEntryChars = 12_000;
    private const string Header = "=== ";

    private static readonly object Gate = new();
    private static readonly UTF8Encoding Utf8 = new(false);
    private static string? _path;

    /// <summary>Raised, once it is written down, when an exception is stopping the program: what is left to do is a last note (see <see cref="RunSentinel"/>).</summary>
    public static event Action<string>? Terminating;

    /// <param name="path">The crash log; null stops writing.</param>
    public static void Initialize(string? path)
    {
        lock (Gate) _path = path;
    }

    /// <param name="source">Where it came from: the handler that caught it.</param>
    /// <param name="terminating">True when the runtime is about to stop the program because of it.</param>
    public static void Record(string source, Exception error, bool terminating)
    {
        var title = terminating ? source + " (the program stops)" : source;
        Log.Error(title, error);

        var entry = new StringBuilder();
        entry.Append(Header).Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)).Append(' ').Append(title).AppendLine(" ===");
        try
        {
            entry.Append("Memory: ").AppendLine(MemorySnapshot.Take().ToString());
        }
        catch (Exception)
        {
            entry.AppendLine("Memory: unknown");
        }

        var text = error.ToString();
        if (text.Length > MaxEntryChars) text = text[..MaxEntryChars] + Environment.NewLine + "(the rest is cut)";
        entry.AppendLine(text).AppendLine();

        Append(entry.ToString());

        if (!terminating) return;

        var listeners = Terminating;
        if (listeners is null) return;

        foreach (var listener in listeners.GetInvocationList().Cast<Action<string>>())
        {
            try
            {
                listener(source);
            }
            catch (Exception)
            {
                // The program is going down already: a note that fails changes nothing, and must not keep the others from theirs.
            }
        }
    }

    private static void Append(string entry)
    {
        lock (Gate)
        {
            if (_path is null) return;
            try
            {
                var dir = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                var info = new FileInfo(_path);
                if (info.Exists && info.Length > MaxBytes) File.Move(_path, _path + ".1", overwrite: true);
                File.AppendAllText(_path, entry, Utf8);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Writing the account of a crash must not be a second one.
            }
        }
    }

    /// <summary>The last entry of the crash log, cut to <paramref name="maxChars"/>; null when there is none.</summary>
    public static string? ReadLast(string path, int maxChars)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Utf8);
            var text = reader.ReadToEnd();
            var at = text.LastIndexOf(Environment.NewLine + Header, StringComparison.Ordinal);
            var start = at >= 0 ? at + Environment.NewLine.Length : text.StartsWith(Header, StringComparison.Ordinal) ? 0 : -1;
            if (start < 0) return null;
            var last = text[start..].TrimEnd();
            return last.Length <= maxChars ? last : last[..maxChars] + Environment.NewLine + "(the rest is cut)";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
