using System.Globalization;
using System.Text;

namespace Arr411.Core.Diagnostics;

/// <summary>
/// The line for the log when a process of the browser behind the web views stops. "It failed" tells nothing: the kind of process, the reason the browser
/// gives, the exit code and what it means are what tells a crash of the page from a lack of memory or a browser that was killed.
/// </summary>
public static class WebFailureText
{
    private static readonly Dictionary<uint, string> Meanings = new()
    {
        [0xC0000005] = "access violation",
        [0xC0000017] = "not enough memory",
        [0xC00000FD] = "stack overflow",
        [0xC000012D] = "the system ran out of memory or of paging file",
        [0xC0000142] = "a library could not start",
        [0xC0000374] = "the heap is corrupted",
        [0xC0000409] = "fast fail (the process stopped itself on purpose)",
    };

    public static string Describe(string kind, string reason, int exitCode, string? process, string? modulePath)
    {
        var code = unchecked((uint)exitCode);
        var text = new StringBuilder();
        text.Append(kind).Append(" (reason: ").Append(reason).Append(", exit code 0x").Append(code.ToString("X8", CultureInfo.InvariantCulture));
        if (Meanings.TryGetValue(code, out var meaning)) text.Append(": ").Append(meaning);
        text.Append(')');

        if (!string.IsNullOrWhiteSpace(process)) text.Append(", process: ").Append(process.Trim());

        // The name of the file only: the folders above it hold the name of the user.
        var module = string.IsNullOrWhiteSpace(modulePath) ? null : Path.GetFileName(modulePath.Trim());
        if (!string.IsNullOrEmpty(module)) text.Append(", failing module: ").Append(module);

        return text.ToString();
    }
}
