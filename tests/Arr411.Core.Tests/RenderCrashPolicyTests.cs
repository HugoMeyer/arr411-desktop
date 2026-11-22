using Arr411.Core.Shell;

namespace Arr411.Core.Tests;

public class RenderCrashPolicyTests
{
    private long _now = 1_000_000;

    private RenderCrashPolicy Policy() => new(() => _now);

    private void Wait(TimeSpan time) => _now += (long)time.TotalMilliseconds;

    [Fact]
    public void The_first_two_stops_reload_and_the_third_within_a_minute_gives_up()
    {
        var policy = Policy();

        Assert.True(policy.ShouldReload());
        Wait(TimeSpan.FromSeconds(10));
        Assert.True(policy.ShouldReload());
        Assert.False(policy.GivenUp);
        Wait(TimeSpan.FromSeconds(10));
        Assert.False(policy.ShouldReload());

        Assert.True(policy.GivenUp);
    }

    [Fact]
    public void Stops_at_once_count_the_same()
    {
        var policy = Policy();

        Assert.True(policy.ShouldReload());
        Assert.True(policy.ShouldReload());
        Assert.False(policy.ShouldReload());
    }

    [Fact]
    public void Once_given_up_it_stays_so_however_long_it_has_been()
    {
        var policy = Policy();
        for (var i = 0; i < RenderCrashPolicy.MaxStops; i++) policy.ShouldReload();

        Wait(TimeSpan.FromHours(2));

        Assert.True(policy.GivenUp);
        Assert.False(policy.ShouldReload());
        Assert.True(policy.GivenUp);
    }

    [Fact]
    public void Stops_that_are_far_enough_apart_never_give_up()
    {
        var policy = Policy();

        for (var i = 0; i < 20; i++)
        {
            Assert.True(policy.ShouldReload());
            Wait(TimeSpan.FromSeconds(40));
        }

        Assert.False(policy.GivenUp);
    }

    [Fact]
    public void A_stop_counts_for_exactly_one_minute()
    {
        var policy = Policy();
        Assert.True(policy.ShouldReload());
        Wait(RenderCrashPolicy.Window - TimeSpan.FromMilliseconds(1));
        Assert.True(policy.ShouldReload());

        // The first stop is a minute old now: only the second one is left in the count.
        Wait(TimeSpan.FromMilliseconds(1));
        Assert.True(policy.ShouldReload());
        Assert.False(policy.GivenUp);

        // One more right away: three stops within the last minute.
        Assert.False(policy.ShouldReload());
    }

    [Fact]
    public void The_last_stop_of_a_minute_is_one_too_many()
    {
        var policy = Policy();
        Assert.True(policy.ShouldReload());
        Wait(TimeSpan.FromSeconds(30));
        Assert.True(policy.ShouldReload());
        Wait(RenderCrashPolicy.Window - TimeSpan.FromSeconds(30) - TimeSpan.FromMilliseconds(1));

        Assert.False(policy.ShouldReload());
    }

    [Fact]
    public void Asking_for_the_page_again_starts_the_count_over()
    {
        var policy = Policy();
        for (var i = 0; i < RenderCrashPolicy.MaxStops; i++) policy.ShouldReload();
        Assert.True(policy.GivenUp);

        policy.Reset();

        Assert.False(policy.GivenUp);
        Assert.True(policy.ShouldReload());
        Assert.True(policy.ShouldReload());
        Assert.False(policy.ShouldReload());
    }

    [Fact]
    public void A_reset_forgets_the_stops_that_were_not_enough_to_give_up()
    {
        var policy = Policy();
        Assert.True(policy.ShouldReload());
        Assert.True(policy.ShouldReload());

        policy.Reset();

        Assert.True(policy.ShouldReload());
        Assert.True(policy.ShouldReload());
        Assert.False(policy.ShouldReload());
    }

    [Fact]
    public void The_clock_of_the_machine_is_the_default()
    {
        var policy = new RenderCrashPolicy();

        Assert.True(policy.ShouldReload());
        Assert.True(policy.ShouldReload());
        Assert.False(policy.ShouldReload());
    }
}
