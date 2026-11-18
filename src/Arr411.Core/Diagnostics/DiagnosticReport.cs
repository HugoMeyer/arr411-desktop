using System.Globalization;
using System.Text;

namespace Arr411.Core.Diagnostics;

/// <summary>What the report says about the program and the computer it runs on.</summary>
/// <param name="Settings">One line per fact about the settings: whether something is set, never its value.</param>
/// <param name="WindowsEvents">What Windows wrote about the program in its own journal (a crash of the runtime or of a native part is only there).</param>
/// <param name="WebComponents">What the web views (the browser processes behind them) hold, apart from the program itself.</param>
public sealed record ReportFacts(
    string Product,
    string Version,
    DateTimeOffset Now,
    DateTimeOffset Started,
    string OperatingSystem,
    string Runtime,
    string? WebView,
    MemorySnapshot Memory,
    PreviousRun? Previous,
    IReadOnlyList<string> Settings,
    string? WindowsEvents,
    ChildProcesses? WebComponents = null);

/// <summary>
/// One text that holds what is needed to understand a problem without asking for more: the versions, the memory, how the last run ended, the last crash
/// and the end of the log. It is made to be pasted in a message, so everything private is taken out of it.
/// </summary>
public static class DiagnosticReport
{
    public const int LogLines = 80;
    private const int CrashChars = 6_000;
    private const int TailBytes = 96 * 1024;

    public static string Build(ReportFacts facts, string logFile, string crashFile, int logLines = LogLines)
    {
        var culture = CultureInfo.InvariantCulture;
        var text = new StringBuilder();
        text.AppendLine(string.Create(culture, $"{facts.Product} diagnostic report"));
        text.AppendLine(string.Create(culture, $"Generated: {facts.Now:yyyy-MM-dd HH:mm:ss zzz}"));

        Section(text, "Application");
        text.AppendLine(string.Create(culture, $"Version: {facts.Version}"));
        text.AppendLine(string.Create(culture, $"Started: {facts.Started:yyyy-MM-dd HH:mm:ss} (up {Humanize.Duration(facts.Now - facts.Started)})"));
        text.AppendLine($"Windows: {facts.OperatingSystem}");
        text.AppendLine($".NET: {facts.Runtime}");
        text.AppendLine($"WebView2: {facts.WebView ?? "not found"}");

        Section(text, "Memory");
        text.AppendLine(facts.Memory.ToString());
        if (facts.WebComponents is not null) text.AppendLine("Web components: " + facts.WebComponents);

        Section(text, "Previous run");
        text.AppendLine(facts.Previous is null ? "It ended cleanly (or there was none)." : facts.Previous.ToString());

        Section(text, "Settings");
        foreach (var setting in facts.Settings) text.AppendLine("- " + setting);

        Section(text, "Last crash");
        text.AppendLine(CrashLog.ReadLast(crashFile, CrashChars) ?? "None recorded.");

        if (!string.IsNullOrWhiteSpace(facts.WindowsEvents))
        {
            Section(text, "Windows events about the program");
            text.AppendLine(facts.WindowsEvents.Trim());
        }

        Section(text, $"Log (last {logLines} lines)");
        var lines = LastLines(logFile, logLines);
        if (lines.Count < logLines) lines.InsertRange(0, LastLines(logFile + ".1", logLines - lines.Count));
        text.AppendLine(lines.Count == 0 ? "The log is empty." : string.Join(Environment.NewLine, lines));

        return Redaction.Clean(text.ToString());
    }

    private static void Section(StringBuilder text, string title) => text.AppendLine().AppendLine($"== {title} ==");

    /// <summary>The last lines of a file that the program may be writing to at this very moment; none when it cannot be read.</summary>
    internal static List<string> LastLines(string path, int count)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var skipped = stream.Length > TailBytes;
            if (skipped) stream.Seek(-TailBytes, SeekOrigin.End);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var lines = reader.ReadToEnd().Split('\n').Select(line => line.TrimEnd('\r')).Where(line => line.Length > 0).ToList();

            // Starting in the middle of the file, the first line is cut.
            if (skipped && lines.Count > 0) lines.RemoveAt(0);
            return lines.Count > count ? lines[^count..] : lines;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
