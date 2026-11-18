using Arr411.Core.Diagnostics;
using Arr411.Core.Logging;

namespace Arr411.Core.Tests;

// CrashLog and Log are static files for the whole process: this class runs alone (see LogTests).
[Collection("Log")]
public sealed class DiagnosticReportTests : IDisposable
{
    private static readonly DateTimeOffset Started = new(2026, 11, 18, 8, 0, 0, TimeSpan.FromHours(1));
    private static readonly DateTimeOffset Now = new(2026, 11, 18, 10, 30, 0, TimeSpan.FromHours(1));

    private readonly TempDir _dir = new();

    private string LogFile => _dir.File(@"logs\arr411.log");

    private string CrashFile => _dir.File(@"logs\crash.log");

    public void Dispose() => _dir.Dispose();

    private static ReportFacts Facts(PreviousRun? previous = null, string? events = null, string[]? settings = null) => new(
        "Arr411 Desktop",
        "0.2.1",
        Now,
        Started,
        "Microsoft Windows 10.0.26100",
        ".NET 10.0.3",
        "131.0.2903.112",
        new MemorySnapshot(912L << 20, 640L << 20, 804L << 20, 768L << 20, 850L << 20, 3, TimeSpan.FromMilliseconds(120), 412, 38, TimeSpan.FromHours(2.5)),
        previous,
        settings ?? ["qBittorrent: configured", "Notifications: on"],
        events,
        new ChildProcesses(3, 420L << 20, [300L << 20, 100L << 20]));

    private void WriteLog(string path, int lines, string prefix = "line")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllLines(path, Enumerable.Range(0, lines).Select(i => $"2026-11-18 09:00:00.000 INFO  {prefix} {i}"));
    }

    [Fact]
    public void The_report_has_its_sections_in_order_with_the_facts_in_them()
    {
        WriteLog(LogFile, 5);

        var report = DiagnosticReport.Build(Facts(), LogFile, CrashFile);

        var headings = new[] { "Arr411 Desktop diagnostic report", "== Application ==", "== Memory ==", "== Previous run ==", "== Settings ==", "== Last crash ==", "== Log (last 80 lines) ==" };
        var at = -1;
        foreach (var heading in headings)
        {
            var next = report.IndexOf(heading, StringComparison.Ordinal);
            Assert.True(next > at, $"'{heading}' is missing or out of order");
            at = next;
        }

        Assert.Contains("Generated: 2026-11-18 10:30:00 +01:00", report);
        Assert.Contains("Version: 0.2.1", report);
        Assert.Contains("Started: 2026-11-18 08:00:00 (up 2 h 30 min)", report);
        Assert.Contains("Windows: Microsoft Windows 10.0.26100", report);
        Assert.Contains(".NET: .NET 10.0.3", report);
        Assert.Contains("WebView2: 131.0.2903.112", report);
        Assert.Contains("private 912 MB", report);
        Assert.Contains("Web components: 3 child processes hold 420 MB (private) in all, the biggest: 300, 100 MB", report);
        Assert.Contains("- qBittorrent: configured", report);
        Assert.Contains("It ended cleanly", report);
        Assert.Contains("None recorded.", report);
        Assert.DoesNotContain("Windows events", report);
    }

    [Fact]
    public void Only_the_last_lines_of_the_log_are_in_it()
    {
        WriteLog(LogFile, 200);

        var report = DiagnosticReport.Build(Facts(), LogFile, CrashFile, logLines: 10);

        Assert.Contains("== Log (last 10 lines) ==", report);
        Assert.Contains("INFO  line 199", report);
        Assert.Contains("INFO  line 190", report);
        Assert.DoesNotContain("INFO  line 189", report);
    }

    [Fact]
    public void A_log_that_has_just_rolled_over_is_completed_from_the_older_file()
    {
        WriteLog(LogFile + ".1", 100, "old");
        WriteLog(LogFile, 3, "new");

        var report = DiagnosticReport.Build(Facts(), LogFile, CrashFile, logLines: 10);

        Assert.Contains("INFO  old 99", report);
        Assert.Contains("INFO  old 93", report);
        Assert.DoesNotContain("INFO  old 92", report);
        Assert.Contains("INFO  new 2", report);
        Assert.True(report.IndexOf("old 99", StringComparison.Ordinal) < report.IndexOf("new 0", StringComparison.Ordinal));
    }

    [Fact]
    public void A_huge_log_is_only_read_at_its_end()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LogFile)!);
        File.WriteAllLines(LogFile, Enumerable.Range(0, 60_000).Select(i => $"2026-11-18 09:00:00.000 INFO  filler line number {i} " + new string('x', 40)));

        var report = DiagnosticReport.Build(Facts(), LogFile, CrashFile);

        Assert.Contains("filler line number 59999 ", report);
        Assert.Contains("filler line number 59920 ", report);
        Assert.DoesNotContain("filler line number 59919 ", report);
    }

    [Fact]
    public void A_missing_log_is_said_to_be_empty()
    {
        var report = DiagnosticReport.Build(Facts(), LogFile, CrashFile);

        Assert.Contains("The log is empty.", report);
    }

    [Fact]
    public void A_previous_run_that_did_not_end_cleanly_is_in_the_report()
    {
        var previous = new PreviousRun(4242, "0.2.0", Started, Now, "private 5800 MB", null);

        var report = DiagnosticReport.Build(Facts(previous), LogFile, CrashFile);

        Assert.Contains("The previous run did not end cleanly: version 0.2.0, process 4242", report);
        Assert.Contains("memory then: private 5800 MB", report);
    }

    [Fact]
    public void The_last_crash_is_in_the_report()
    {
        CrashLog.Initialize(CrashFile);
        try
        {
            CrashLog.Record("UnhandledException", new InvalidOperationException("it blew up"), terminating: true);
        }
        finally
        {
            CrashLog.Initialize(null);
        }

        var report = DiagnosticReport.Build(Facts(), LogFile, CrashFile);

        Assert.Contains("UnhandledException (the program stops)", report);
        Assert.Contains("it blew up", report);
        Assert.DoesNotContain("None recorded.", report);
    }

    [Fact]
    public void What_windows_wrote_about_the_program_is_in_the_report_when_there_is_something()
    {
        var report = DiagnosticReport.Build(Facts(events: "Faulting application name: Arr411.exe, exception code: 0xc0000005"), LogFile, CrashFile);

        Assert.Contains("== Windows events about the program ==", report);
        Assert.Contains("exception code: 0xc0000005", report);
    }

    [Fact]
    public void Nothing_private_is_left_in_it()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Directory.CreateDirectory(Path.GetDirectoryName(LogFile)!);
        File.WriteAllLines(LogFile,
        [
            "2026-11-18 09:00:00.000 INFO  login failed for http://admin:hunter2@192.168.1.5:8080 password=hunter2",
            $"2026-11-18 09:00:01.000 INFO  saved to {Path.Combine(profile, "Downloads", "x.torrent")}",
        ]);

        var report = DiagnosticReport.Build(Facts(), LogFile, CrashFile);

        Assert.DoesNotContain("hunter2", report);
        Assert.Contains("http://***@192.168.1.5:8080", report);
        Assert.Contains("password=***", report);
        Assert.DoesNotContain(profile, report, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("%USERPROFILE%", report);
    }

    [Fact]
    public void The_log_can_be_read_while_the_program_is_writing_to_it()
    {
        Log.Initialize(LogFile);
        try
        {
            Log.Info("first");
            using var writer = new FileStream(LogFile, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);

            var report = DiagnosticReport.Build(Facts(), LogFile, CrashFile);

            Assert.Contains("INFO  first", report);
        }
        finally
        {
            Log.Initialize(null);
        }
    }
}
