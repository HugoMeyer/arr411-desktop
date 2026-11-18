using System.Diagnostics;
using System.Globalization;

namespace Arr411.Core.Diagnostics;

/// <summary>
/// What the process holds at one moment. Read afterwards in the log, it tells a slow leak from a one-time spike, and a program that is short of memory
/// from one that is stuck in the garbage collector.
/// </summary>
public sealed record MemorySnapshot(
    long PrivateBytes,
    long WorkingSetBytes,
    long ManagedBytes,
    long LargeObjectBytes,
    long CommittedBytes,
    int FullCollections,
    TimeSpan CollectionPause,
    int Handles,
    int Threads,
    TimeSpan Uptime)
{
    public static MemorySnapshot Take()
    {
        using var process = Process.GetCurrentProcess();
        var gc = GC.GetGCMemoryInfo();
        return new MemorySnapshot(
            process.PrivateMemorySize64,
            process.WorkingSet64,
            GC.GetTotalMemory(forceFullCollection: false),
            gc.GenerationInfo.Length > 3 ? gc.GenerationInfo[3].SizeAfterBytes : 0,
            gc.TotalCommittedBytes,
            GC.CollectionCount(2),
            GC.GetTotalPauseDuration(),
            process.HandleCount,
            process.Threads.Count,
            DateTime.Now - process.StartTime);
    }

    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture,
        $"private {Humanize.Megabytes(PrivateBytes)} MB, working set {Humanize.Megabytes(WorkingSetBytes)} MB, managed {Humanize.Megabytes(ManagedBytes)} MB "
        + $"(large objects {Humanize.Megabytes(LargeObjectBytes)} MB after the last collection), committed {Humanize.Megabytes(CommittedBytes)} MB, "
        + $"{FullCollections} full collections (paused {CollectionPause.TotalMilliseconds:0} ms in all), {Handles} handles, {Threads} threads, "
        + $"up {Humanize.Duration(Uptime)}");
}
