using System.Diagnostics;
using System.Text;

namespace Arr411.Core.Logging;

/// <summary>
/// Small rolling file log (arr411.log, rotated at 2 MB, one older file kept). Never log secrets: no headers, request bodies or full URLs.
/// Does nothing until <see cref="Initialize"/> is called (unit tests), or once it is given no path.
/// </summary>
public static class Log
{
    internal const long MaxBytes = 2 * 1024 * 1024;
    private const int MaxFrames = 12;
    private const int MaxDepth = 3;
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

    public static void Error(string message, Exception? ex = null)
    {
        if (ex is null)
        {
            Write("ERROR", message);
            return;
        }

        var text = new StringBuilder(message).Append(": ");
        Describe(text, ex, 0);
        Write("ERROR", text.ToString());
    }

    // The exception, then what it was caused by (the inner exceptions, a few levels down, and every one of those an AggregateException holds): a failure
    // that is only "One or more errors occurred" says nothing.
    private static void Describe(StringBuilder text, Exception ex, int depth)
    {
        if (depth > 0) text.Append(Environment.NewLine).Append(' ', depth * 2).Append("caused by: ");
        text.Append(ex.GetType().Name).Append(": ").Append(ex.Message).Append(Frames(ex, depth));
        if (depth + 1 >= MaxDepth) return;

        if (ex is AggregateException aggregate)
        {
            foreach (var inner in aggregate.InnerExceptions.Take(MaxDepth)) Describe(text, inner, depth + 1);
        }
        else if (ex.InnerException is { } cause)
        {
            Describe(text, cause, depth + 1);
        }
    }

    // Where it failed, by method names only (no file paths, no arguments): enough to find the line, and nothing private.
    private static string Frames(Exception ex, int depth)
    {
        var text = new StringBuilder();
        foreach (var frame in new StackTrace(ex, fNeedFileInfo: false).GetFrames().Take(MaxFrames))
        {
            if (frame.GetMethod() is { } method)
            {
                text.Append(Environment.NewLine).Append(' ', depth * 2).Append("    at ").Append(method.DeclaringType?.Name).Append('.').Append(method.Name);
            }
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
