using Arr411.Core.Diagnostics;

namespace Arr411.Core.Tests;

public sealed class UncaughtExceptionsTests
{
    private readonly List<(string Source, Exception Error)> _recorded = [];
    private readonly List<string> _warned = [];

    private UncaughtExceptions Hooks(int max = 3) => new((source, error) => _recorded.Add((source, error)), _warned.Add, max);

    private static UnobservedTaskExceptionEventArgs Unobserved(params Exception[] errors) => new(new AggregateException(errors));

    [Fact]
    public void A_task_that_failed_unseen_is_recorded_and_marked_as_seen()
    {
        var args = Unobserved(new InvalidOperationException("boom"));

        Hooks().OnUnobservedTask(this, args);

        Assert.True(args.Observed);
        var (source, error) = Assert.Single(_recorded);
        Assert.Equal("Unobserved task exception", source);
        Assert.Contains("boom", error.ToString());
    }

    [Fact]
    public void A_task_that_was_only_cancelled_is_not_a_failure()
    {
        var hooks = Hooks();

        hooks.OnUnobservedTask(this, Unobserved(new TaskCanceledException()));
        hooks.OnUnobservedTask(this, Unobserved(new OperationCanceledException(), new TaskCanceledException()));

        Assert.Empty(_recorded);
        Assert.Empty(_warned);
    }

    [Fact]
    public void A_cancellation_next_to_a_real_failure_does_not_hide_it()
    {
        Hooks().OnUnobservedTask(this, Unobserved(new TaskCanceledException(), new IOException("disk")));

        Assert.Single(_recorded);
    }

    [Fact]
    public void Only_the_first_ones_are_recorded_and_the_log_says_so_once()
    {
        var hooks = Hooks(max: 3);

        for (var i = 0; i < 8; i++) hooks.OnUnobservedTask(this, Unobserved(new InvalidOperationException("n" + i)));

        Assert.Equal(3, _recorded.Count);
        Assert.Single(_warned);
        Assert.Contains("More than 3 unobserved task exceptions", _warned[0]);
    }

    [Fact]
    public void An_exception_of_the_window_is_recorded_as_such()
    {
        Hooks().OnWindowThread(new InvalidOperationException("in a handler"));

        var (source, error) = Assert.Single(_recorded);
        Assert.Equal("Unhandled UI exception", source);
        Assert.Contains("in a handler", error.ToString());
    }

    [Fact]
    public void Each_kind_has_its_own_limit_so_that_a_loop_of_one_does_not_hide_the_other()
    {
        var hooks = Hooks(max: 2);

        for (var i = 0; i < 6; i++) hooks.OnWindowThread(new InvalidOperationException("w" + i));
        hooks.OnUnobservedTask(this, Unobserved(new IOException("task")));

        Assert.Equal(["Unhandled UI exception", "Unhandled UI exception", "Unobserved task exception"], _recorded.Select(entry => entry.Source));
        var warning = Assert.Single(_warned);
        Assert.Contains("More than 2 UI thread exceptions", warning);
    }

    [Fact]
    public void Whatever_was_thrown_becomes_an_exception()
    {
        var real = new InvalidOperationException("x");

        Assert.Same(real, UncaughtExceptions.AsException(real));
        Assert.Contains("a string", UncaughtExceptions.AsException("a string").Message);
        Assert.Contains("null", UncaughtExceptions.AsException(null).Message);
    }
}
