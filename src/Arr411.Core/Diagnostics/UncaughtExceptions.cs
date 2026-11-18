using Arr411.Core.Logging;

namespace Arr411.Core.Diagnostics;

/// <summary>
/// The exceptions that nobody caught and that do not stop the program by themselves: a task that nobody waits for fails without a word in .NET, and
/// an event handler of the window that throws leaves the window running. What looks like a random crash may start with one of those. They go to the
/// crash log, but only the first few of each kind: a loop that fails a thousand times must not bury the rest of the story.
/// </summary>
public sealed class UncaughtExceptions
{
    internal const int DefaultMax = 20;

    private readonly Action<string, Exception> _record;
    private readonly Action<string> _warn;
    private readonly int _max;
    private int _unobserved;
    private int _onWindow;

    public UncaughtExceptions()
        : this((source, error) => CrashLog.Record(source, error, terminating: false), Log.Warn, DefaultMax)
    {
    }

    internal UncaughtExceptions(Action<string, Exception> record, Action<string> warn, int max)
    {
        _record = record;
        _warn = warn;
        _max = max;
    }

    /// <summary>What <see cref="AppDomain.UnhandledException"/> carries is an object: nearly always an exception, but nothing says it must be one.</summary>
    public static Exception AsException(object? thrown) =>
        thrown as Exception ?? new InvalidOperationException("Something that is not an exception was thrown: " + (thrown?.ToString() ?? "null"));

    public void OnUnobservedTask(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        e.SetObserved();

        // A task that was cancelled and that nobody awaited is not a failure.
        if (e.Exception.Flatten().InnerExceptions.All(inner => inner is OperationCanceledException)) return;

        Record(ref _unobserved, "Unobserved task exception", "unobserved task exceptions", e.Exception);
    }

    /// <summary>An exception that an event handler of the window let through: the window carries on, but the account of it must exist.</summary>
    public void OnWindowThread(Exception error) => Record(ref _onWindow, "Unhandled UI exception", "UI thread exceptions", error);

    private void Record(ref int counter, string source, string what, Exception error)
    {
        var number = Interlocked.Increment(ref counter);
        if (number <= _max) _record(source, error);
        else if (number == _max + 1) _warn($"More than {_max} {what}: the next ones are not recorded");
    }
}
