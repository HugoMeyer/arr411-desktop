using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace Arr411.Core.Diagnostics;

/// <summary>
/// What Windows wrote in its Application journal about the program. When the runtime or a native part of the process dies, or when Windows sees the
/// memory of the program grow without end, the program is not there to say anything: this journal is the only place that does.
/// </summary>
public static class WindowsEvents
{
    private const string EventNamespace = "http://schemas.microsoft.com/win/2004/08/events/event";
    private const string Mention = "Arr411";
    private const int Days = 14;
    private const int Scanned = 400;
    private const int MaxEvents = 10;
    private const int MaxChars = 1_500;

    /// <summary>One line per event of the last two weeks that is about the program, the newest first. Says so when there is none, or when Windows did not answer.</summary>
    public static string Read() => Read(TimeSpan.FromSeconds(10));

    internal static string Read(TimeSpan timeout)
    {
        var xml = Ask(Query(Days), Scanned, timeout);
        if (xml is null) return "Windows did not answer.";

        var lines = Parse(xml, Mention, MaxEvents);
        return lines.Count == 0 ? string.Create(CultureInfo.InvariantCulture, $"None in the last {Days} days.") : string.Join(Environment.NewLine, lines);
    }

    /// <summary>The events that tell how a program stopped, or why Windows took it for a sick one.</summary>
    internal static string Query(int days) => string.Create(
        CultureInfo.InvariantCulture,
        $"*[System[(Provider[@Name='Application Error'] or Provider[@Name='.NET Runtime'] or Provider[@Name='Application Hang'] or Provider[@Name='Windows Error Reporting']) and TimeCreated[timediff(@SystemTime) <= {days * 86_400_000L}]]]");

    /// <summary>Runs wevtutil.exe and returns what it prints, or null when it fails or takes too long.</summary>
    internal static string? Ask(string query, int count, TimeSpan timeout)
    {
        try
        {
            var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "wevtutil.exe"))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8,
            };
            start.ArgumentList.Add("qe");
            start.ArgumentList.Add("Application");
            start.ArgumentList.Add("/q:" + query);
            start.ArgumentList.Add(string.Create(CultureInfo.InvariantCulture, $"/c:{count}"));
            start.ArgumentList.Add("/rd:true");
            start.ArgumentList.Add("/f:xml");

            using var process = Process.Start(start);
            if (process is null) return null;

            // Both streams are read as they fill: a reply of a few hundred kilobytes would otherwise stop wevtutil on a full pipe.
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(timeout))
            {
                process.Kill(entireProcessTree: true);
                return null;
            }

            if (!Task.WhenAll(output, errors).Wait(TimeSpan.FromSeconds(2))) return null;
            return process.ExitCode == 0 ? output.Result : null;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or AggregateException)
        {
            return null;
        }
    }

    /// <summary>The events of the text that wevtutil printed (a series of Event elements) that mention <paramref name="mention"/>, one line each, at most <paramref name="max"/>.</summary>
    internal static IReadOnlyList<string> Parse(string xml, string mention, int max)
    {
        XDocument document;
        try
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            using var reader = XmlReader.Create(new StringReader("<Events>" + xml + "</Events>"), settings);
            document = XDocument.Load(reader);
        }
        catch (XmlException)
        {
            return [];
        }

        XNamespace ns = EventNamespace;
        var lines = new List<string>();
        foreach (var record in document.Root!.Elements(ns + "Event"))
        {
            var items = record.Element(ns + "EventData")?.Elements(ns + "Data").ToList() ?? [];
            var values = items.Select(item => item.Value.Trim()).ToList();
            if (!values.Any(value => value.Contains(mention, StringComparison.OrdinalIgnoreCase))) continue;

            var named = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var item in items)
            {
                if (item.Attribute("Name") is { } name) named[name.Value] = item.Value.Trim();
            }

            var system = record.Element(ns + "System");
            var provider = system?.Element(ns + "Provider")?.Attribute("Name")?.Value ?? "?";
            var id = system?.Element(ns + "EventID")?.Value ?? "?";
            var time = system?.Element(ns + "TimeCreated")?.Attribute("SystemTime")?.Value;
            lines.Add($"{When(time)} {provider} #{id}: {Describe(provider, named, values)}");
            if (lines.Count >= max) break;
        }

        return lines;
    }

    private static string Describe(string provider, Dictionary<string, string> named, List<string> values)
    {
        if (provider == "Application Error" && named.ContainsKey("AppName"))
        {
            return $"{Get(named, "AppName")} {Get(named, "AppVersion")} crashed in {Get(named, "ModuleName")} {Get(named, "ModuleVersion")}, exception code {Get(named, "ExceptionCode")}, offset {Get(named, "FaultingOffset")}";
        }

        // The signature of the report (P1...P10) is what matters; the rest is paths of temporary files.
        if (provider == "Windows Error Reporting" && named.TryGetValue("EventName", out var report))
        {
            var signature = string.Join(' ', Enumerable.Range(1, 10).Select(number => Get(named, "P" + number.ToString(CultureInfo.InvariantCulture))).Where(value => value.Length > 0));
            return $"{report} {signature}{Meaning(report)}";
        }

        var text = string.Join(' ', string.Join(" | ", values.Where(value => value.Length > 0)).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return text.Length <= MaxChars ? text : text[..MaxChars] + " (the rest is cut)";
    }

    private static string Get(Dictionary<string, string> named, string key) => named.GetValueOrDefault(key) ?? string.Empty;

    private static string Meaning(string report) => report switch
    {
        "RADAR_PRE_LEAK_64" or "RADAR_PRE_LEAK_32" => " (Windows saw the memory of the program grow and grow: a leak)",
        "APPCRASH" => " (a crash in native code)",
        "CLR20r3" => " (an unhandled .NET exception)",
        _ => string.Empty,
    };

    private static string When(string? time) =>
        DateTimeOffset.TryParse(time, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
            ? at.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
            : "?";
}
