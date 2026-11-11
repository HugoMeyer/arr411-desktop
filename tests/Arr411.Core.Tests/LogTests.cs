using System.Runtime.CompilerServices;
using Arr411.Core.Logging;

namespace Arr411.Core.Tests;

// The log is one static file for the whole process: these tests run alone, and switch it off again afterwards.
[CollectionDefinition("Log", DisableParallelization = true)]
public sealed class LogCollection;

[Collection("Log")]
public sealed class LogTests : IDisposable
{
    private const int MegabytePlus = 400_000;

    private readonly TempDir _dir = new();

    private string LogFile => _dir.File(@"logs\arr411.log");

    public void Dispose()
    {
        Log.Initialize(null);
        _dir.Dispose();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Explode() => throw new InvalidOperationException("it blew up");

    [Fact]
    public void Each_line_tells_when_how_serious_and_what()
    {
        Log.Initialize(LogFile);

        Log.Info("hello");
        Log.Warn("careful");
        Log.Error("broken");

        var lines = File.ReadAllLines(LogFile);
        Assert.Equal(3, lines.Length);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} INFO  hello$", lines[0]);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} WARN  careful$", lines[1]);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} ERROR broken$", lines[2]);
    }

    [Fact]
    public void An_error_names_the_exception_and_the_methods_it_went_through_and_no_file_path()
    {
        Log.Initialize(LogFile);
        var failure = Assert.Throws<InvalidOperationException>(Explode);

        Log.Error("could not do it", failure);

        var text = File.ReadAllText(LogFile);
        Assert.Contains("ERROR could not do it: InvalidOperationException: it blew up", text);
        Assert.Contains("    at LogTests.Explode", text);
        Assert.DoesNotContain(".cs", text);
        Assert.DoesNotContain(":line", text);
    }

    [Fact]
    public void The_folder_of_the_log_is_made_when_it_is_missing()
    {
        var file = _dir.File(@"a\b\c\arr411.log");
        Log.Initialize(file);

        Log.Info("first");

        Assert.Contains("first", File.ReadAllText(file));
    }

    [Fact]
    public void Nothing_is_written_once_the_log_is_switched_off()
    {
        Log.Initialize(LogFile);
        Log.Info("on");

        Log.Initialize(null);
        Log.Info("off");

        var text = File.ReadAllText(LogFile);
        Assert.Contains("on", text);
        Assert.DoesNotContain("off", text);
    }

    [Fact]
    public void The_log_rolls_over_to_a_second_file_once_it_holds_more_than_a_megabyte()
    {
        Log.Initialize(LogFile);
        var filler = new string('x', MegabytePlus);
        Log.Info("first " + filler);
        Log.Info("second " + filler);
        Log.Info("third " + filler);
        Assert.False(File.Exists(LogFile + ".1"));

        Log.Info("fourth");

        Assert.Contains("third", File.ReadAllText(LogFile + ".1"));
        var current = File.ReadAllText(LogFile);
        Assert.Contains("fourth", current);
        Assert.DoesNotContain("third", current);
    }

    [Fact]
    public void Only_the_last_full_file_is_kept()
    {
        Log.Initialize(LogFile);
        var filler = new string('x', MegabytePlus);
        for (var round = 0; round < 2; round++)
        {
            for (var i = 0; i < 3; i++) Log.Info($"round {round} line {i} {filler}");
            Log.Info($"round {round} end");
        }

        Log.Info("last");

        var older = File.ReadAllText(LogFile + ".1");
        Assert.Contains("round 1 line 2", older);
        Assert.DoesNotContain("round 0 line 2", older);
        Assert.False(File.Exists(LogFile + ".2"));
    }

    [Fact]
    public void A_log_that_cannot_be_made_never_stops_the_app()
    {
        var blocker = _dir.File("blocker");
        File.WriteAllText(blocker, "a file where the folder should be");
        Log.Initialize(Path.Combine(blocker, "arr411.log"));

        Log.Info("nobody will read this");
        Log.Error("nor this", new IOException("disk full"));

        Assert.True(File.Exists(blocker));
    }

    [Fact]
    public void A_log_file_another_program_holds_open_is_skipped_and_written_again_afterwards()
    {
        Log.Initialize(LogFile);
        Log.Info("before");

        using (new FileStream(LogFile, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Log.Info("while held");
        }

        Log.Info("after");

        var text = File.ReadAllText(LogFile);
        Assert.Contains("before", text);
        Assert.DoesNotContain("while held", text);
        Assert.Contains("after", text);
    }

    [Fact]
    public async Task Lines_written_at_the_same_time_stay_whole()
    {
        Log.Initialize(LogFile);

        await Task.WhenAll(Enumerable.Range(0, 8).Select(writer => Task.Run(() =>
        {
            for (var i = 0; i < 100; i++) Log.Info($"writer {writer} line {i} " + new string('y', 200));
        })));

        var lines = File.ReadAllLines(LogFile);
        Assert.Equal(800, lines.Length);
        Assert.All(lines, line => Assert.Matches(@"^\S+ \S+ INFO  writer \d line \d+ y{200}$", line));
    }
}
