using Arr411.Core.Shell;

namespace Arr411.Core.Tests;

public class TabStateTests
{
    [Fact]
    public void Unknown_tabs_are_refused()
    {
        var state = new TabState();

        Assert.Equal(TabChange.None, state.Show("nope", true));
        Assert.Equal("", state.Current);
    }

    [Fact]
    public void Showing_a_tab_remembers_the_previous_one()
    {
        var state = new TabState();
        state.Show(Tabs.Radarr, true);

        var change = state.Show(Tabs.Settings, true);

        Assert.True(change.Changed);
        Assert.Equal(Tabs.Settings, state.Current);
        Assert.Equal(Tabs.Radarr, state.Previous);
    }

    [Fact]
    public void Showing_the_same_tab_again_changes_nothing()
    {
        var state = new TabState();
        state.Show(Tabs.Qbit, true);

        Assert.Equal(TabChange.None, state.Show(Tabs.Qbit, true));
        Assert.Equal(Tabs.C411, state.Previous);
    }

    [Fact]
    public void The_spa_is_told_only_once_it_is_ready_and_out_of_step()
    {
        var state = new TabState();
        Assert.False(state.Show(Tabs.Sonarr, true).TellSpa);

        Assert.True(state.OnSpaReady(out var tab));
        Assert.Equal(Tabs.Sonarr, tab);
        Assert.True(state.Show(Tabs.Lidarr, true).TellSpa);
        Assert.False(state.Show(Tabs.Lidarr, true).TellSpa);
    }

    [Fact]
    public void A_tab_chosen_by_the_spa_itself_is_not_sent_back_to_it()
    {
        var state = new TabState();
        state.Show(Tabs.Radarr, true);
        state.OnSpaReady(out _);

        var change = state.Show(Tabs.Qbit, tellSpa: false);

        Assert.True(change.Changed);
        Assert.False(change.TellSpa);
        Assert.False(state.Show(Tabs.Qbit, true).TellSpa);
    }

    [Fact]
    public void Asking_for_the_tab_at_boot_puts_both_sides_in_step()
    {
        var state = new TabState();
        state.Show(Tabs.Radarr, true);

        Assert.Equal(Tabs.Radarr, state.TabForSpa());
        Assert.False(state.OnSpaReady(out _));
    }

    [Fact]
    public void A_new_page_is_not_ready_until_it_says_so()
    {
        var state = new TabState();
        state.OnSpaReady(out _);
        Assert.True(state.SpaReady);

        state.OnSpaStarted();

        Assert.False(state.SpaReady);
    }

    [Fact]
    public void Back_from_settings_goes_to_the_tab_it_was_opened_from()
    {
        var state = new TabState();
        state.Show(Tabs.Radarr, true);
        state.Show(Tabs.Settings, true);
        Assert.Equal(Tabs.Radarr, state.BackTarget());

        state.Show(Tabs.Sonarr, true);
        Assert.Equal(Tabs.C411, state.BackTarget());
    }
}

public class StartTabTests
{
    private static FakeConfig Configured(params (string Key, string Value)[] more)
    {
        var config = new FakeConfig { ["radarrUrl"] = "http://nas:7878" };
        foreach (var (key, value) in more) config[key] = value;
        return config;
    }

    [Fact]
    public void A_first_launch_opens_the_settings()
    {
        Assert.Equal(Tabs.Settings, StartTab.Resolve(new FakeConfig { ["startTab"] = "radarr" }, null));
    }

    [Fact]
    public void Blank_addresses_do_not_count_as_configured()
    {
        var config = new FakeConfig { ["qbUrl"] = "   ", ["radarrUrl"] = "" };

        Assert.Equal(Tabs.Settings, StartTab.Resolve(config, null));
    }

    [Fact]
    public void Any_configured_service_is_enough_and_c411_is_the_default()
    {
        Assert.Equal(Tabs.C411, StartTab.Resolve(Configured(), null));
        Assert.Equal(Tabs.C411, StartTab.Resolve(new FakeConfig { ["lidarrUrl"] = "http://x" }, null));
    }

    [Fact]
    public void The_chosen_start_tab_is_used()
    {
        Assert.Equal(Tabs.Sonarr, StartTab.Resolve(Configured(("startTab", "sonarr")), null));
    }

    [Fact]
    public void Last_means_the_tab_used_last_time()
    {
        var config = Configured(("startTab", "last"));

        Assert.Equal(Tabs.Qbit, StartTab.Resolve(config, Tabs.Qbit));
        Assert.Equal(Tabs.C411, StartTab.Resolve(config, null));
        Assert.Equal(Tabs.C411, StartTab.Resolve(config, Tabs.Settings));
        Assert.Equal(Tabs.C411, StartTab.Resolve(config, "bogus"));
    }

    [Theory]
    [InlineData("settings")]
    [InlineData("bogus")]
    [InlineData("")]
    public void Settings_and_unknown_values_fall_back_to_c411(string startTab)
    {
        Assert.Equal(Tabs.C411, StartTab.Resolve(Configured(("startTab", startTab)), null));
    }

    [Fact]
    public void An_explicit_request_wins_over_everything()
    {
        Assert.Equal(Tabs.Lidarr, StartTab.Resolve(new FakeConfig(), Tabs.Qbit, Tabs.Lidarr));
        Assert.Equal(Tabs.Settings, StartTab.Resolve(Configured(), null, Tabs.Settings));
    }

    [Fact]
    public void An_invalid_request_is_ignored()
    {
        Assert.Equal(Tabs.C411, StartTab.Resolve(Configured(), null, "bogus"));
    }
}

public class TabsTests
{
    [Theory]
    [InlineData("c411", true)]
    [InlineData("radarr", true)]
    [InlineData("sonarr", true)]
    [InlineData("lidarr", true)]
    [InlineData("qbit", true)]
    [InlineData("settings", true)]
    [InlineData("C411", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_the_known_names_are_tabs(string? name, bool expected)
    {
        Assert.Equal(expected, Tabs.IsTab(name));
    }

    [Fact]
    public void The_bar_has_five_tabs_without_settings()
    {
        Assert.Equal(["c411", "radarr", "sonarr", "lidarr", "qbit"], Tabs.Bar);
        Assert.False(Tabs.IsRemembered(Tabs.Settings));
        Assert.True(Tabs.IsRemembered(Tabs.Qbit));
    }
}

public class ExternalLinksTests
{
    [Theory]
    [InlineData("https://c411.org/torrents?q=x", LinkKind.C411)]
    [InlineData("https://www.c411.org/", LinkKind.C411)]
    [InlineData("HTTPS://C411.ORG/", LinkKind.C411)]
    [InlineData("  https://c411.org/  ", LinkKind.C411)]
    [InlineData("https://notc411.org/", LinkKind.Web)]
    [InlineData("https://c411.org.evil.example/", LinkKind.Web)]
    [InlineData("http://example.com/page", LinkKind.Web)]
    [InlineData("magnet:?xt=urn:btih:abc&dn=x", LinkKind.Magnet)]
    [InlineData("file:///C:/Windows/System32/calc.exe", LinkKind.Rejected)]
    [InlineData("javascript:alert(1)", LinkKind.Rejected)]
    [InlineData("ms-msdt:/id PCWDiagnostic", LinkKind.Rejected)]
    [InlineData("ftp://example.com/", LinkKind.Rejected)]
    [InlineData("not a url", LinkKind.Rejected)]
    [InlineData("", LinkKind.Rejected)]
    [InlineData(null, LinkKind.Rejected)]
    public void Links_are_sorted_and_dangerous_ones_refused(string? url, LinkKind expected)
    {
        Assert.Equal(expected, ExternalLinks.Classify(url));
    }

    [Theory]
    [InlineData("https://c411.org/", "https://c411.org/")]
    [InlineData("https://c411.org", "https://c411.org/")]
    [InlineData("https://www.c411.org/torrents?q=x", "https://www.c411.org/torrents?q=x")]
    [InlineData("http://c411.org/x?q=1", "https://c411.org/x?q=1")]
    [InlineData("HTTP://WWW.C411.ORG/x", "https://www.c411.org/x")]
    [InlineData("https://c411.org:443/x", "https://c411.org/x")]
    [InlineData("http://c411.org:80/x", "https://c411.org/x")]
    [InlineData("  https://c411.org/torrents/abc#top  ", "https://c411.org/torrents/abc#top")]
    public void Only_the_real_site_is_loaded_in_the_C411_tab(string url, string expected)
    {
        Assert.Equal(expected, ExternalLinks.C411Page(url)?.AbsoluteUri);
    }

    [Theory]
    [InlineData("https://c411.org:8443/")]
    [InlineData("http://c411.org:8080/")]
    [InlineData("https://c411.org:80/")]
    [InlineData("http://c411.org:443/")]
    [InlineData("https://user@c411.org/")]
    [InlineData("https://user:pass@c411.org/")]
    [InlineData("https://c411.org@evil.example/")]
    [InlineData("https://c411.org.evil.example/")]
    [InlineData("https://evil.example/c411.org")]
    [InlineData("https://notc411.org/")]
    [InlineData("ftp://c411.org/")]
    [InlineData("magnet:?xt=urn:btih:abc&dn=x")]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///C:/c411.org/")]
    [InlineData("//c411.org/x")]
    [InlineData("/torrents/1")]
    [InlineData("not a url")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Anything_else_is_not_for_the_C411_tab(string? url)
    {
        Assert.Null(ExternalLinks.C411Page(url));
    }
}
