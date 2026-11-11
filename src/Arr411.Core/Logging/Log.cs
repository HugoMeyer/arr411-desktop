using System.Diagnostics;
using System.Text;

namespace Arr411.Core.Logging;

/// <summary>
/// Small rolling file log (arr411.log, rotated at 1 MB). Never log secrets: no headers, request bodies or full URLs.
/// Does nothing until <see cref="Initialize"/> is called (unit tests), or once it is given no path.
/// </summary>
public static class Log
{
    private const long MaxBytes = 1024 * 1024;
    private static readonly object Gate = new();
    private static readonly UTF8Encoding Utf8 = new(false);
    private static string? _path;

    /// <param name="path">The log file; null stops writing.</param>
    public static void Initialize(string? path)
    {
        lock (Gate) _path = path;
    }

    public static void Info(string message) => Write("INFO ", message);

    public static void Warn(string message) => Write("WARN ", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message}: {ex.GetType().Name}: {ex.Message}{Frames(ex)}");

    // Where it failed, by method names only (no file paths, no arguments): enough to find the line, and nothing private.
    private static string Frames(Exception ex)
    {
        var text = new StringBuilder();
        foreach (var frame in new StackTrace(ex, fNeedFileInfo: false).GetFrames().Take(8))
        {
            if (frame.GetMethod() is { } method) text.Append(Environment.NewLine).Append("    at ").Append(method.DeclaringType?.Name).Append('.').Append(method.Name);
        }

        return text.ToString();
    }

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {message}{Environment.NewLine}";
        Debug.Write(line);

        lock (Gate)
        {
            if (_path is null) return;
            try
            {
                var dir = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                var info = new FileInfo(_path);
                if (info.Exists && info.Length > MaxBytes) File.Move(_path, _path + ".1", overwrite: true);
                File.AppendAllText(_path, line, Utf8);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A log must never take the app down.
            }
        }
    }
}
