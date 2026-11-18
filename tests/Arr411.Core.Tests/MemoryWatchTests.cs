using Arr411.Core.Diagnostics;

namespace Arr411.Core.Tests;

public sealed class MemoryWatchTests
{
    private const long Mb = MemoryWatch.Megabyte;
    private const long Gb = MemoryWatch.Gigabyte;

    private readonly List<string> _info = [];
    private readonly List<string> _warn = [];
    private readonly List<MemorySnapshot> _beats = [];
    private DateTimeOffset _now = new(2026, 11, 18, 9, 0, 0, TimeSpan.Zero);
    private long _private = 300 * Mb;
    private long _managed = 40 * Mb;
    private long _managedAfterCompacting = 20 * Mb;
    private int _compactions;

    private MemoryWatch Watch() => new(
        () => new MemorySnapshot(_private, _private, _managed, 0, _private, 1, TimeSpan.Zero, 100, 20, TimeSpan.FromMinutes(5)),
        () =>
        {
            _compactions++;
            _private -= _managed - _managedAfterCompacting;
            _managed = _managedAfterCompacting;
        },
        _info.Add,
        _warn.Add,
        _beats.Add,
        () => _now,
        period: null);

    private void Minutes(int minutes) => _now += TimeSpan.FromMinutes(minutes);

    [Fact]
    public void Every_tick_tells_that_the_program_is_alive()
    {
        using var watch = Watch();

        watch.Tick();
        Minutes(1);
        watch.Tick();

        Assert.Equal(2, _beats.Count);
    }

    [Fact]
    public void The_memory_is_logged_at_the_start_then_every_quarter_of_an_hour()
    {
        using var watch = Watch();

        watch.Tick();
        Assert.Single(_info, line => line.StartsWith("Memory: private 300 MB"));

        Minutes(14);
        watch.Tick();
        Assert.Single(_info);

        Minutes(1);
        watch.Tick();
        Assert.Equal(2, _info.Count);
    }

    [Fact]
    public void A_warning_the_first_time_the_memory_passes_one_two_and_four_gigabytes()
    {
        using var watch = Watch();

        _private = 1 * Gb + 200 * Mb;
        watch.Tick();
        watch.Tick();
        Assert.Single(_warn);
        Assert.Contains("over 1 GB", _warn[0]);

        _private = 2 * Gb + 500 * Mb;
        watch.Tick();
        Assert.Equal(2, _warn.Count);
        Assert.Contains("over 2 GB", _warn[1]);

        _private = 5 * Gb;
        watch.Tick();
        Assert.Equal(3, _warn.Count);
        Assert.Contains("over 4 GB", _warn[2]);
    }

    [Fact]
    public void It_says_when_the_memory_comes_back_down_and_warns_again_if_it_climbs_again()
    {
        using var watch = Watch();
        _private = 3 * Gb;
        watch.Tick();
        Assert.Single(_warn);

        _private = 500 * Mb;
        watch.Tick();
        Assert.Contains(_info, line => line.StartsWith("The memory use is back under 1 GB"));

        _private = 3 * Gb;
        watch.Tick();
        Assert.Equal(2, _warn.Count);
    }

    [Fact]
    public void High_managed_memory_is_given_back_and_the_log_says_how_much()
    {
        using var watch = Watch();
        _managed = 700 * Mb;
        _private = 1 * Gb;

        watch.Tick();

        Assert.Equal(1, _compactions);
        Assert.Contains(_info, line => line.StartsWith("Memory given back: managed 700 -> 20 MB, private 1024 -> 344 MB"));
    }

    [Fact]
    public void Low_managed_memory_is_left_alone()
    {
        using var watch = Watch();
        _managed = 100 * Mb;

        watch.Tick();
        watch.Tick();

        Assert.Equal(0, _compactions);
    }

    [Fact]
    public void What_stays_after_a_collection_is_in_use_so_it_is_not_collected_again_until_it_has_grown_by_half()
    {
        using var watch = Watch();
        _managedAfterCompacting = 200 * Mb;
        _managed = 400 * Mb;
        watch.Tick();
        Assert.Equal(1, _compactions);

        _managed = 290 * Mb;
        watch.Tick();
        Assert.Equal(1, _compactions);

        _managed = 310 * Mb;
        watch.Tick();
        Assert.Equal(2, _compactions);
    }

    [Fact]
    public void The_log_line_says_what_the_web_components_hold_and_a_failure_to_count_them_does_not_hide_the_line()
    {
        var children = new ChildProcesses(2, 700 * Mb, [500 * Mb, 200 * Mb]);
        using (var watch = new MemoryWatch(
            () => new MemorySnapshot(300 * Mb, 300 * Mb, 40 * Mb, 0, 300 * Mb, 1, TimeSpan.Zero, 100, 20, TimeSpan.FromMinutes(5)),
            () => { },
            _info.Add,
            _warn.Add,
            _beats.Add,
            () => _now,
            period: null,
            children: () => children))
        {
            watch.Tick();
        }

        Assert.Single(_info, line => line.StartsWith("Memory: private 300 MB") && line.EndsWith("2 child processes hold 700 MB (private) in all, the biggest: 500, 200 MB"));

        _info.Clear();
        using (var watch = new MemoryWatch(
            () => new MemorySnapshot(300 * Mb, 300 * Mb, 40 * Mb, 0, 300 * Mb, 1, TimeSpan.Zero, 100, 20, TimeSpan.FromMinutes(5)),
            () => { },
            _info.Add,
            _warn.Add,
            _beats.Add,
            () => _now,
            period: null,
            children: () => throw new InvalidOperationException("no list")))
        {
            watch.Tick();
        }

        Assert.Single(_info, line => line.StartsWith("Memory: private 300 MB") && line.EndsWith("the web components could not be counted (InvalidOperationException)"));
        Assert.Empty(_warn);
    }

    [Fact]
    public void A_probe_that_fails_is_reported_and_never_thrown()
    {
        using var watch = new MemoryWatch(
            () => throw new InvalidOperationException("no counters"),
            () => { },
            _info.Add,
            _warn.Add,
            _beats.Add,
            () => _now,
            period: null);

        watch.Tick();

        Assert.Single(_warn, line => line.Contains("memory watch failed") && line.Contains("no counters"));
    }

    [Fact]
    public void A_snapshot_of_this_process_is_plausible_and_readable()
    {
        var snapshot = MemorySnapshot.Take();

        Assert.True(snapshot.PrivateBytes > 10 * Mb);
        Assert.True(snapshot.ManagedBytes > 0);
        Assert.True(snapshot.Threads > 0);
        Assert.Matches(@"^private \d+ MB, working set \d+ MB, managed \d+ MB \(large objects \d+ MB after the last collection\), committed \d+ MB, \d+ full collections \(paused \d+ ms in all\), \d+ handles, \d+ threads, up ", snapshot.ToString());
    }

    [Theory]
    [InlineData(5, "5 s")]
    [InlineData(65, "1 min 5 s")]
    [InlineData(3 * 3600 + 20 * 60, "3 h 20 min")]
    [InlineData(2 * 86400 + 3 * 3600, "2 d 3 h")]
    [InlineData(-4, "0 s")]
    public void Durations_are_told_with_their_two_biggest_units(int seconds, string expected)
    {
        Assert.Equal(expected, Humanize.Duration(TimeSpan.FromSeconds(seconds)));
    }
}
