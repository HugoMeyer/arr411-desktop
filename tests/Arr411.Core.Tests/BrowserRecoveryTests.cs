using Arr411.Core.Shell;

namespace Arr411.Core.Tests;

public class BrowserRecoveryTests
{
    public static TheoryData<string> AllTabs() => [.. Tabs.Bar, Tabs.Settings];

    [Fact]
    public void The_program_starts_again_when_the_browser_had_run_for_a_while()
    {
        Assert.True(BrowserRecovery.ShouldRestart(BrowserRecovery.MinimumRun));
        Assert.True(BrowserRecovery.ShouldRestart(TimeSpan.FromHours(3)));
    }

    [Fact]
    public void It_does_not_when_the_browser_had_only_just_started()
    {
        Assert.False(BrowserRecovery.ShouldRestart(TimeSpan.Zero));
        Assert.False(BrowserRecovery.ShouldRestart(TimeSpan.FromSeconds(5)));
        Assert.False(BrowserRecovery.ShouldRestart(BrowserRecovery.MinimumRun - TimeSpan.FromMilliseconds(1)));
        Assert.False(BrowserRecovery.ShouldRestart(TimeSpan.FromSeconds(-1)));
    }

    [Fact]
    public void Another_minimum_replaces_the_default_one()
    {
        Assert.True(BrowserRecovery.ShouldRestart(TimeSpan.Zero, TimeSpan.Zero));
        Assert.False(BrowserRecovery.ShouldRestart(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)));
        Assert.True(BrowserRecovery.ShouldRestart(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10)));
    }

    [Theory]
    [MemberData(nameof(AllTabs))]
    public void The_window_comes_back_on_the_tab_it_was_on(string tab)
    {
        var request = LaunchRequest.Parse(BrowserRecovery.RestartArguments(hidden: false, tab), @"C:\");

        Assert.Equal(tab, request.Tab);
        Assert.False(request.Background);
        Assert.Equal(0, request.Ignored);
        Assert.True(request.HasWork);
    }

    [Theory]
    [MemberData(nameof(AllTabs))]
    public void A_window_that_was_out_of_sight_stays_out_of_sight(string tab)
    {
        var request = LaunchRequest.Parse(BrowserRecovery.RestartArguments(hidden: true, tab), @"C:\");

        Assert.True(request.Background);
        Assert.False(request.HasWork);
        Assert.Equal(0, request.Ignored);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nope")]
    [InlineData("--quit")]
    [InlineData("magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567")]
    public void Only_a_known_tab_ever_reaches_the_command_line(string? tab)
    {
        Assert.Empty(BrowserRecovery.RestartArguments(hidden: false, tab));
    }
}
