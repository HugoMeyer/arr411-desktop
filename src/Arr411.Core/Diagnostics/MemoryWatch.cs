using System.Runtime;
using Arr411.Core.Logging;

namespace Arr411.Core.Diagnostics;

/// <summary>
/// Looks at the memory of the process once a minute. A line goes to the log every quarter of an hour, and a warning when it first passes 1, 2 and 4 GB.
/// When the managed memory is high, a full collection that compacts the large object heap gives it back: the huge texts of a big library leave holes in
/// that heap, which the collector would not clean up before it is much fuller than that.
/// </summary>
public sealed class MemoryWatch : IDisposable
{
    internal const long Megabyte = 1024 * 1024;
    internal const long Gigabyte = 1024 * Megabyte;
    internal const long CompactFloor = 128 * Megabyte;
    internal static readonly TimeSpan SnapshotEvery = TimeSpan.FromMinutes(15);
    private static readonly long[] Levels = [Gigabyte, 2 * Gigabyte, 4 * Gigabyte];

    private readonly Func<MemorySnapshot> _take;
    private readonly Action _compact;
    private readonly Action<string> _info;
    private readonly Action<string> _warn;
    private readonly Action<MemorySnapshot> _beat;
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<ChildProcesses>? _children;
    private readonly object _gate = new();
    private readonly Timer? _timer;
    private DateTimeOffset _lastSnapshot = DateTimeOffset.MinValue;
    private int _level;
    private long _compactAbove = CompactFloor;

    /// <param name="beat">Told of every snapshot, once a minute: it is how the program tells it is still alive.</param>
    public MemoryWatch(Action<MemorySnapshot> beat)
        : this(MemorySnapshot.Take, CompactNow, Log.Info, Log.Warn, beat, () => DateTimeOffset.Now, TimeSpan.FromMinutes(1), ChildProcesses.Take)
    {
    }

    internal MemoryWatch(
        Func<MemorySnapshot> take,
        Action compact,
        Action<string> info,
        Action<string> warn,
        Action<MemorySnapshot> beat,
        Func<DateTimeOffset> now,
        TimeSpan? period,
        Func<ChildProcesses>? children = null)
    {
        _take = take;
        _compact = compact;
        _info = info;
        _warn = warn;
        _beat = beat;
        _now = now;
        _children = children;
        if (period is { } every) _timer = new Timer(_ => Tick(), null, every, every);
    }

    public void Dispose() => _timer?.Dispose();

    internal void Tick()
    {
        lock (_gate)
        {
            try
            {
                var now = _now();
                var snapshot = _take();
                _beat(snapshot);
                WatchLevels(snapshot);
                if (now - _lastSnapshot >= SnapshotEvery)
                {
                    _lastSnapshot = now;
                    _info("Memory: " + Describe(snapshot));
                }

                if (snapshot.ManagedBytes > _compactAbove) Compact(snapshot);
            }
            catch (Exception ex)
            {
                // A timer that throws takes the whole program down: the watch is only there to help.
                _warn($"The memory watch failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    /// <summary>The snapshot, and what the web views hold on top of it: a page that grew too big is in their processes, not in this one.</summary>
    private string Describe(MemorySnapshot snapshot)
    {
        if (_children is null) return snapshot.ToString();
        try
        {
            return $"{snapshot}; {_children()}";
        }
        catch (Exception ex)
        {
            return $"{snapshot}; the web components could not be counted ({ex.GetType().Name})";
        }
    }

    private void WatchLevels(MemorySnapshot snapshot)
    {
        var level = Levels.Count(limit => snapshot.PrivateBytes >= limit);
        if (level > _level) _warn($"The memory use is over {Levels[level - 1] / Gigabyte} GB: {Describe(snapshot)}");
        else if (level < _level) _info($"The memory use is back under {Levels[level] / Gigabyte} GB: {Describe(snapshot)}");
        _level = level;
    }

    private void Compact(MemorySnapshot before)
    {
        _compact();
        var after = _take();
        _info(
            $"Memory given back: managed {Humanize.Megabytes(before.ManagedBytes)} -> {Humanize.Megabytes(after.ManagedBytes)} MB, "
            + $"private {Humanize.Megabytes(before.PrivateBytes)} -> {Humanize.Megabytes(after.PrivateBytes)} MB");

        // What stays after a collection is in use: collecting again before it has grown by half would only cost time.
        _compactAbove = Math.Max(CompactFloor, after.ManagedBytes + after.ManagedBytes / 2);
    }

    private static void CompactNow()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
    }
}
