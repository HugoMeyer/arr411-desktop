using System.Runtime.ExceptionServices;
using Arr411.Core.Shell;

namespace Arr411.Core.Tests;

public class UiThreadTests
{
    // A thread of its own, so that it cannot be the one the test runs on whatever the scheduler does.
    private static void OnAnotherThread(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.Start();
        thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    [Fact]
    public void Work_asked_on_the_thread_that_made_it_runs_at_once()
    {
        var posted = new List<Action>();
        var ui = new UiThread(posted.Add);
        var ran = false;

        ui.Run(() => ran = true);

        Assert.True(ran);
        Assert.Empty(posted);
        Assert.True(ui.IsCurrent);
    }

    [Fact]
    public void Work_asked_from_another_thread_is_posted_and_not_run_there()
    {
        var posted = new List<Action>();
        var ui = new UiThread(posted.Add);
        var ran = false;
        var current = true;

        OnAnotherThread(() =>
        {
            current = ui.IsCurrent;
            ui.Run(() => ran = true);
        });

        Assert.False(current);
        Assert.False(ran);
        posted.Single()();
        Assert.True(ran);
    }

    [Theory]
    [InlineData(typeof(ObjectDisposedException))]
    [InlineData(typeof(InvalidOperationException))]
    public void A_window_that_cannot_take_the_work_leaves_the_caller_in_peace(Type failure)
    {
        var ui = new UiThread(_ => throw (Exception)Activator.CreateInstance(failure, "closing")!);
        var ran = false;

        OnAnotherThread(() => ui.Run(() => ran = true));

        Assert.False(ran);
    }

    [Fact]
    public void Other_failures_of_the_window_are_not_hidden()
    {
        var ui = new UiThread(_ => throw new NotSupportedException());

        Assert.Throws<NotSupportedException>(() => OnAnotherThread(() => ui.Run(() => { })));
    }

    [Fact]
    public void A_failure_of_the_work_run_at_once_reaches_the_caller()
    {
        var ui = new UiThread(_ => { });

        Assert.Throws<NotSupportedException>(() => ui.Run(() => throw new NotSupportedException()));
    }
}
