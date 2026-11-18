using System.Diagnostics;
using Arr411.Core.Diagnostics;

namespace Arr411.Core.Tests;

public sealed class RunSentinelTests : IDisposable
{
    private static readonly DateTimeOffset Started = new(2026, 11, 18, 8, 30, 0, TimeSpan.FromHours(1));

    private readonly TempDir _dir = new();

    private string SessionFile => _dir.File(@"logs\running.txt");

    public void Dispose() => _dir.Dispose();

    private static MemorySnapshot Memory() => new(912L << 20, 640L << 20, 804L << 20, 768L << 20, 850L << 20, 3, TimeSpan.FromMilliseconds(120), 412, 38, TimeSpan.FromMinutes(7));

    private PreviousRun? Leftover(bool running = false) => RunSentinel.Leftover(SessionFile, (_, _) => running);

    [Fact]
    public void A_run_that_ends_the_right_way_leaves_nothing()
    {
        var sentinel = new RunSentinel(SessionFile);
        sentinel.Begin("0.2.1", 4242, Started);
        Assert.True(File.Exists(SessionFile));

        sentinel.End();

        Assert.False(File.Exists(SessionFile));
        Assert.Null(Leftover());
    }

    [Fact]
    public void A_run_that_was_stopped_leaves_what_it_knew_about_itself()
    {
        var sentinel = new RunSentinel(SessionFile);
        sentinel.Begin("0.2.1", 4242, Started);
        sentinel.Beat(Memory());

        var previous = Leftover();

        Assert.NotNull(previous);
        Assert.Equal(4242, previous.Pid);
        Assert.Equal("0.2.1", previous.Version);
        Assert.Equal(Started, previous.Started);
        Assert.NotNull(previous.LastSeen);
        Assert.StartsWith("private 912 MB", previous.Memory);
        Assert.Null(previous.Crash);
    }

    [Fact]
    public void A_crash_that_was_caught_is_remembered()
    {
        var sentinel = new RunSentinel(SessionFile);
        sentinel.Begin("0.2.1", 4242, Started);

        sentinel.Crashed("UnhandledException");

        Assert.Equal("UnhandledException", Leftover()?.Crash);
    }

    [Fact]
    public void A_beat_that_comes_after_the_end_does_not_bring_the_file_back()
    {
        var sentinel = new RunSentinel(SessionFile);
        sentinel.Begin("0.2.1", 4242, Started);
        sentinel.End();

        sentinel.Beat(Memory());
        sentinel.Crashed("late");

        Assert.False(File.Exists(SessionFile));
    }

    [Fact]
    public void After_a_crash_the_end_leaves_the_file_for_the_next_start()
    {
        var sentinel = new RunSentinel(SessionFile);
        sentinel.Begin("0.2.1", 4242, Started);
        sentinel.Crashed("UnhandledException");

        sentinel.End();
        sentinel.Beat(Memory());

        Assert.Equal("UnhandledException", Leftover()?.Crash);
    }

    [Fact]
    public void A_sentinel_can_begin_again_after_it_ended()
    {
        var sentinel = new RunSentinel(SessionFile);
        sentinel.Begin("0.2.1", 4242, Started);
        sentinel.End();

        sentinel.Begin("0.2.1", 4243, Started);
        sentinel.Beat(Memory());

        Assert.Equal(4243, Leftover()?.Pid);
    }

    [Fact]
    public void A_run_that_is_still_going_is_not_a_leftover()
    {
        var sentinel = new RunSentinel(SessionFile);
        sentinel.Begin("0.2.1", 4242, Started);

        Assert.Null(Leftover(running: true));
    }

    [Fact]
    public void Without_a_file_or_with_one_that_means_nothing_there_is_no_previous_run()
    {
        Assert.Null(Leftover());

        Directory.CreateDirectory(Path.GetDirectoryName(SessionFile)!);
        File.WriteAllText(SessionFile, "nothing to see here");
        Assert.Null(Leftover());
    }

    [Fact]
    public void The_real_check_knows_this_process_is_running_and_that_another_number_is_not()
    {
        using var self = Process.GetCurrentProcess();
        var sentinel = new RunSentinel(SessionFile);

        sentinel.Begin("0.2.1", Environment.ProcessId, new DateTimeOffset(self.StartTime));
        Assert.Null(RunSentinel.Leftover(SessionFile));

        sentinel.Begin("0.2.1", int.MaxValue, Started);
        Assert.NotNull(RunSentinel.Leftover(SessionFile));
    }

    [Fact]
    public void A_number_taken_again_by_another_process_since_is_not_the_same_run()
    {
        var sentinel = new RunSentinel(SessionFile);

        sentinel.Begin("0.2.1", Environment.ProcessId, DateTimeOffset.Now.AddDays(-3));

        Assert.NotNull(RunSentinel.Leftover(SessionFile));
    }

    [Fact]
    public void It_reads_as_a_sentence()
    {
        var sentinel = new RunSentinel(SessionFile);
        sentinel.Begin("0.2.0", 4242, Started);
        sentinel.Beat(Memory());
        sentinel.Crashed("TaskScheduler.UnobservedTaskException");

        var text = Leftover()?.ToString();

        Assert.NotNull(text);
        Assert.StartsWith("The previous run did not end cleanly: version 0.2.0, process 4242, started 2026-11-18 08:30:00, last seen alive ", text);
        Assert.Contains("it recorded a crash (TaskScheduler.UnobservedTaskException)", text);
        Assert.Contains("memory then: private 912 MB", text);
    }

    [Fact]
    public void A_file_that_cannot_be_written_or_deleted_never_stops_the_program()
    {
        var blocker = _dir.File("blocker");
        File.WriteAllText(blocker, "a file where the folder should be");
        var sentinel = new RunSentinel(Path.Combine(blocker, "running.txt"));

        sentinel.Begin("0.2.1", 4242, Started);
        sentinel.Beat(Memory());
        sentinel.Crashed("x");
        sentinel.End();

        Assert.True(File.Exists(blocker));
    }
}
