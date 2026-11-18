using System.Globalization;
using Arr411.Core.Diagnostics;

namespace Arr411.Core.Tests;

public sealed class WindowsEventsTests
{
    private const string Namespace = "xmlns='http://schemas.microsoft.com/win/2004/08/events/event'";

    // The shape of what wevtutil prints for each event: a bare Event element, attributes in single quotes.
    private static string Event(string provider, int id, string utc, string data) =>
        $"<Event {Namespace}><System><Provider Name='{provider}'/><EventID>{id}</EventID><TimeCreated SystemTime='{utc}'/></System><EventData>{data}</EventData></Event>";

    private static string Crash(string app, string utc = "2026-11-08T19:16:55.0072451Z") =>
        Event(
            "Application Error",
            1000,
            utc,
            $"<Data Name='AppName'>{app}</Data><Data Name='AppVersion'>0.2.0.0</Data><Data Name='AppTimeStamp'>6ada0000</Data><Data Name='ModuleName'>KERNELBASE.dll</Data>" +
            "<Data Name='ModuleVersion'>10.0.26100.9711</Data><Data Name='ModuleTimeStamp'>fb9071fb</Data><Data Name='ExceptionCode'>e0434352</Data><Data Name='FaultingOffset'>00000000000c408a</Data>" +
            $"<Data Name='AppPath'>C:\\Tools\\{app}</Data>");

    private static string Leak(string utc = "2026-11-15T17:38:49.7833233Z") =>
        Event(
            "Windows Error Reporting",
            1001,
            utc,
            "<Data Name='Bucket'>1349654686415781569</Data><Data Name='EventName'>RADAR_PRE_LEAK_64</Data><Data Name='Response'>Non disponible</Data>" +
            "<Data Name='P1'>Arr411.exe</Data><Data Name='P2'>0.2.0.0</Data><Data Name='P3'>10.0.26200.2.0.0</Data><Data Name='P4'></Data>" +
            "<Data Name='AttachedFiles'>\n\\\\?\\C:\\ProgramData\\Microsoft\\Windows\\WER\\Temp\\WER.08c2d26a.tmp.csv</Data><Data Name='StorePath'>NULL</Data>");

    private static string Local(string utc) =>
        DateTimeOffset.Parse(utc, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    [Fact]
    public void A_native_crash_of_the_program_is_told_in_one_line()
    {
        var lines = WindowsEvents.Parse(Crash("Arr411.exe"), "Arr411", 10);

        Assert.Equal(
            [$"{Local("2026-11-08T19:16:55.0072451Z")} Application Error #1000: Arr411.exe 0.2.0.0 crashed in KERNELBASE.dll 10.0.26100.9711, exception code e0434352, offset 00000000000c408a"],
            lines);
    }

    [Fact]
    public void Windows_saying_that_the_memory_grows_and_grows_is_told_without_the_paths_of_its_temporary_files()
    {
        var lines = WindowsEvents.Parse(Leak(), "Arr411", 10);

        var line = Assert.Single(lines);
        Assert.Equal(
            $"{Local("2026-11-15T17:38:49.7833233Z")} Windows Error Reporting #1001: RADAR_PRE_LEAK_64 Arr411.exe 0.2.0.0 10.0.26200.2.0.0 (Windows saw the memory of the program grow and grow: a leak)",
            line);
        Assert.DoesNotContain("ProgramData", line);
    }

    [Fact]
    public void Only_the_events_about_the_program_are_kept_and_they_stay_in_the_order_of_the_journal()
    {
        var xml = Crash("Other.exe") + Leak("2026-11-16T10:00:00Z") + Crash("Other.exe") + Crash("Arr411.exe", "2026-11-10T08:00:00Z");

        var lines = WindowsEvents.Parse(xml, "Arr411", 10);

        Assert.Equal(2, lines.Count);
        Assert.Contains("RADAR_PRE_LEAK_64", lines[0]);
        Assert.Contains("Arr411.exe 0.2.0.0 crashed", lines[1]);
    }

    [Fact]
    public void The_mention_is_found_whatever_the_case_and_wherever_the_event_says_it()
    {
        var xml = Event(".NET Runtime", 1026, "2026-11-08T19:16:55Z", "<Data>Application: arr411.EXE\nDescription: The process was terminated due to an unhandled exception.</Data>");

        var line = Assert.Single(WindowsEvents.Parse(xml, "Arr411", 10));

        Assert.EndsWith(".NET Runtime #1026: Application: arr411.EXE Description: The process was terminated due to an unhandled exception.", line);
    }

    [Fact]
    public void A_long_text_is_put_on_one_line_and_cut()
    {
        var xml = Event(".NET Runtime", 1026, "2026-11-08T19:16:55Z", "<Data>Application: Arr411.exe\n   at A.B()\n   at C.D()\nMessage: " + new string('m', 5_000) + "</Data>");

        var line = Assert.Single(WindowsEvents.Parse(xml, "Arr411", 10));

        Assert.DoesNotContain('\n', line);
        Assert.Contains("Application: Arr411.exe at A.B() at C.D() Message: mmm", line);
        Assert.EndsWith("(the rest is cut)", line);
        Assert.True(line.Length < 1_700, $"{line.Length} characters");
    }

    [Fact]
    public void Not_more_than_asked_are_kept()
    {
        var xml = string.Concat(Enumerable.Range(1, 5).Select(day => Crash("Arr411.exe", $"2026-11-0{day}T08:00:00Z")));

        Assert.Equal(2, WindowsEvents.Parse(xml, "Arr411", 2).Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("this is not xml <")]
    [InlineData("<?xml version='1.0'?><Event")]
    [InlineData("<!DOCTYPE Events [<!ENTITY boom 'Arr411'>]><Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'/>")]
    public void Text_that_cannot_be_read_gives_nothing(string xml)
    {
        Assert.Empty(WindowsEvents.Parse(xml, "Arr411", 10));
    }

    [Fact]
    public void An_event_with_no_time_and_no_provider_is_still_told()
    {
        var xml = $"<Event {Namespace}><EventData><Data>Arr411 stopped</Data></EventData></Event>";

        Assert.Equal(["? ? #?: Arr411 stopped"], WindowsEvents.Parse(xml, "Arr411", 10));
    }

    [Fact]
    public void The_question_put_to_Windows_is_one_that_wevtutil_accepts()
    {
        var answer = WindowsEvents.Ask(WindowsEvents.Query(14), 1, TimeSpan.FromSeconds(30));

        Assert.NotNull(answer);
        Assert.DoesNotContain("Failed", answer);
    }

    [Fact]
    public void A_question_that_wevtutil_refuses_gives_null()
    {
        Assert.Null(WindowsEvents.Ask("*[System[", 1, TimeSpan.FromSeconds(30)));
    }
}
