using System.Runtime.CompilerServices;
using Arr411.Core.Diagnostics;
using Arr411.Core.Logging;

namespace Arr411.Core.Tests;

// The crash log and the log are static files for the whole process: these tests run alone, and switch both off afterwards.
[Collection("Log")]
public sealed class CrashLogTests : IDisposable
{
    private readonly TempDir _dir = new();

    private string CrashFile => _dir.File(@"logs\crash.log");

    public void Dispose()
    {
        CrashLog.Initialize(null);
        Log.Initialize(null);
        _dir.Dispose();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Explode() => throw new InvalidOperationException("it blew up", new IOException("the disk said no"));

    private static InvalidOperationException Failure() => Assert.Throws<InvalidOperationException>(Explode);

    [Fact]
    public void A_crash_is_written_in_full_with_the_memory_of_that_moment()
    {
        CrashLog.Initialize(CrashFile);
        var failure = Failure();

        CrashLog.Record("UnhandledException", failure, terminating: true);

        var text = File.ReadAllText(CrashFile);
        Assert.Matches(@"^=== \d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} UnhandledException \(the program stops\) ===", text);
        Assert.Contains("Memory: private ", text);
        Assert.Contains(failure.ToString(), text);
        Assert.Contains("the disk said no", text);
    }

    [Fact]
    public void An_exception_that_does_not_stop_the_program_says_so_by_saying_nothing_about_it()
    {
        CrashLog.Initialize(CrashFile);

        CrashLog.Record("ThreadException", Failure(), terminating: false);

        var text = File.ReadAllText(CrashFile);
        Assert.Contains(" ThreadException ===", text);
        Assert.DoesNotContain("the program stops", text);
    }

    [Fact]
    public void The_normal_log_gets_a_line_as_well()
    {
        var logFile = _dir.File(@"logs\arr411.log");
        Log.Initialize(logFile);
        CrashLog.Initialize(CrashFile);

        CrashLog.Record("UnobservedTaskException", Failure(), terminating: false);

        Assert.Contains("ERROR UnobservedTaskException: InvalidOperationException: it blew up", File.ReadAllText(logFile));
    }

    [Fact]
    public void A_very_long_account_is_cut()
    {
        CrashLog.Initialize(CrashFile);

        CrashLog.Record("ThreadException", new InvalidOperationException(new string('m', 50_000)), terminating: false);

        var text = File.ReadAllText(CrashFile);
        Assert.True(text.Length < CrashLog.MaxEntryChars + 1_000, $"{text.Length} characters");
        Assert.Contains("(the rest is cut)", text);
    }

    [Fact]
    public void The_file_rolls_over_to_a_second_one_when_it_is_full_and_keeps_only_that_one()
    {
        CrashLog.Initialize(CrashFile);
        var big = new InvalidOperationException(new string('m', 10_000));

        for (var i = 0; i < 120; i++) CrashLog.Record("ThreadException", big, terminating: false);

        Assert.True(File.Exists(CrashFile + ".1"));
        Assert.False(File.Exists(CrashFile + ".2"));
        Assert.True(new FileInfo(CrashFile).Length < CrashLog.MaxBytes + 20_000);
    }

    [Fact]
    public void The_last_entry_is_the_one_that_is_read_back()
    {
        CrashLog.Initialize(CrashFile);
        CrashLog.Record("First", new InvalidOperationException("number one"), terminating: false);
        CrashLog.Record("Second", new InvalidOperationException("number two"), terminating: false);
        CrashLog.Record("Third", new InvalidOperationException("number three"), terminating: true);

        var last = CrashLog.ReadLast(CrashFile, 5_000);

        Assert.NotNull(last);
        Assert.StartsWith("=== ", last);
        Assert.Contains("Third (the program stops)", last);
        Assert.Contains("number three", last);
        Assert.DoesNotContain("number two", last);
    }

    [Fact]
    public void The_entry_that_is_read_back_can_be_cut_short()
    {
        CrashLog.Initialize(CrashFile);
        CrashLog.Record("Only", new InvalidOperationException(new string('m', 3_000)), terminating: false);

        var last = CrashLog.ReadLast(CrashFile, 500);

        Assert.NotNull(last);
        Assert.EndsWith("(the rest is cut)", last);
        Assert.True(last.Length < 600);
    }

    [Fact]
    public void Nothing_to_read_gives_null()
    {
        Assert.Null(CrashLog.ReadLast(CrashFile, 500));

        Directory.CreateDirectory(Path.GetDirectoryName(CrashFile)!);
        File.WriteAllText(CrashFile, "not an entry");
        Assert.Null(CrashLog.ReadLast(CrashFile, 500));
    }

    [Fact]
    public void A_crash_log_that_cannot_be_written_never_makes_things_worse()
    {
        var blocker = _dir.File("blocker");
        File.WriteAllText(blocker, "a file where the folder should be");
        CrashLog.Initialize(Path.Combine(blocker, "crash.log"));

        CrashLog.Record("UnhandledException", Failure(), terminating: true);

        Assert.True(File.Exists(blocker));
    }

    [Fact]
    public void Nothing_is_written_once_it_is_switched_off()
    {
        CrashLog.Initialize(CrashFile);
        CrashLog.Initialize(null);

        CrashLog.Record("UnhandledException", Failure(), terminating: true);

        Assert.False(File.Exists(CrashFile));
    }

    [Fact]
    public void Whoever_listens_is_told_when_the_program_stops_and_only_then()
    {
        CrashLog.Initialize(CrashFile);
        var told = new List<string>();
        void Listen(string source) => told.Add(source);
        CrashLog.Terminating += Listen;
        try
        {
            CrashLog.Record("ThreadException", Failure(), terminating: false);
            CrashLog.Record("UnhandledException", Failure(), terminating: true);
        }
        finally
        {
            CrashLog.Terminating -= Listen;
        }

        Assert.Equal(["UnhandledException"], told);
        Assert.Contains("UnhandledException (the program stops)", File.ReadAllText(CrashFile));
    }

    [Fact]
    public void A_listener_that_fails_does_not_keep_the_others_from_being_told_and_the_crash_log_may_be_off()
    {
        CrashLog.Initialize(null);
        var told = 0;
        void Fails(string source) => throw new InvalidOperationException("the note failed");
        void Counts(string source) => told++;
        CrashLog.Terminating += Fails;
        CrashLog.Terminating += Counts;
        try
        {
            CrashLog.Record("UnhandledException", Failure(), terminating: true);
        }
        finally
        {
            CrashLog.Terminating -= Fails;
            CrashLog.Terminating -= Counts;
        }

        Assert.Equal(1, told);
    }
}
