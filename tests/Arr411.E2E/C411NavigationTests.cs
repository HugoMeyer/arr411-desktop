namespace Arr411.E2E;

/// <summary>Where the links, the new windows and the permission requests of the C411 tab lead: the site stays in the tab, everything else is the user's choice.</summary>
[Trait("Category", "E2E")]
public sealed class C411NavigationTests(C411App fixture) : IClassFixture<C411App>
{
    private const string AnotherSiteRefused = "A navigation of the C411 tab to another site was refused";
    private const string NavigationRefused = "A navigation of the C411 tab was refused";
    private const string LaunchRefused = "A launch of another application by the C411 tab was refused";
    private const string PopupRefused = "A popup of the C411 tab was refused";
    private const string LinkRefused = "A link was refused by the C411 tab";
    private const string LinkIgnored = "A link of the C411 tab was ignored: another one was just opened";
    private const string PermissionDenied = "A permission request of the C411 tab was denied: ";

    private AppProcess App => fixture.App;

    private FakeC411 Site => fixture.Site;

    [Fact]
    public async Task A_link_inside_the_site_stays_in_the_tab()
    {
        var tab = await App.OpenAsync("/");
        var opened = App.OpenedLinks().Count;

        await tab.ClickAsync("#torrent");

        await tab.WaitForAsync($"location.pathname === '/torrents/{FakeC411.Hash}'");
        Assert.Equal(opened, App.OpenedLinks().Count);
        Assert.Equal(AppProcess.ViewCount, await App.PageCountAsync());
    }

    [Fact]
    public async Task A_link_to_another_site_goes_to_the_browser_of_the_user()
    {
        var tab = await App.OpenAsync("/");
        var opened = App.OpenedLinks().Count;

        await WaitForTurnAsync();
        await tab.ClickAsync("#outside");

        await Wait.UntilAsync(() => App.OpenedLinks().Count > opened, "the link reaching the browser");
        Assert.Equal("https://elsewhere.invalid/outside", App.OpenedLinks()[opened]);
        Assert.Equal(1, App.OpenedLinks().Count - opened);

        // The tab did not follow it.
        Assert.Equal("/", await tab.EvaluateAsync<string>("location.pathname"));
        Assert.Equal(AppProcess.ViewCount, await App.PageCountAsync());
    }

    [Fact]
    public async Task A_page_cannot_send_the_tab_to_another_site_by_itself()
    {
        var tab = await App.OpenAsync("/");
        var opened = App.OpenedLinks().Count;
        var refused = App.CountInLog(AnotherSiteRefused);

        // Nobody clicked: the page does it alone, as an advertisement would.
        await tab.EvaluateAsync("location.href = 'https://elsewhere.invalid/x'");

        await App.WaitForLogAsync(AnotherSiteRefused, count: refused + 1);
        Assert.Equal(opened, App.OpenedLinks().Count);
        Assert.Equal("/", await tab.EvaluateAsync<string>("location.pathname"));
    }

    [Fact]
    public async Task A_page_cannot_fill_the_browser_with_tabs()
    {
        var tab = await App.OpenAsync("/");
        var opened = App.OpenedLinks().Count;
        var ignored = App.CountInLog(LinkIgnored);

        await WaitForTurnAsync();
        await tab.ClickAsync("#outside");
        await tab.ClickAsync("#outside");

        await App.WaitForLogAsync(LinkIgnored, count: ignored + 1);
        Assert.Equal(1, App.OpenedLinks().Count - opened);
    }

    [Fact]
    public async Task A_magnet_link_opens_the_add_sheet_of_qbittorrent_in_the_web_app()
    {
        var tab = await App.OpenAsync("/");
        var opened = App.OpenedLinks().Count;
        var magnet = $"magnet:?xt=urn:btih:{FakeC411.Hash}";

        await WaitForTurnAsync();
        await tab.ClickAsync("#magnet");

        await App.Page.WaitForAsync("App.state.tab === 'qbit'");
        await App.Page.WaitForAsync($"[...document.querySelectorAll('textarea.input')].some(area => area.value.includes({Js.Quote(magnet)}))");

        // The window shows the web app now, and the link did not go to the browser.
        await App.Page.WaitForAsync("document.visibilityState === 'visible'");
        Assert.Equal(opened, App.OpenedLinks().Count);
        await App.Page.EvaluateAsync("App.back()");
    }

    [Fact]
    public async Task A_popup_the_page_opens_by_itself_is_refused()
    {
        var tab = await App.OpenAsync("/");
        var opened = App.OpenedLinks().Count;
        var refused = App.CountInLog(PopupRefused);

        await tab.EvaluateAsync("void window.open('https://elsewhere.invalid/popup')");

        await App.WaitForLogAsync(PopupRefused, count: refused + 1);
        Assert.Equal(opened, App.OpenedLinks().Count);
        Assert.Equal(AppProcess.ViewCount, await App.PageCountAsync());
    }

    [Fact]
    public async Task A_link_that_asks_for_a_new_window_shows_the_page_of_the_site_in_the_tab()
    {
        var tab = await App.OpenAsync("/");

        await tab.ClickAsync("#blank-c411");

        await tab.WaitForAsync($"location.pathname === '/torrents/{FakeC411.Hash}'");
        Assert.Equal(AppProcess.ViewCount, await App.PageCountAsync());
    }

    [Fact]
    public async Task A_link_that_asks_for_a_new_window_to_another_site_goes_to_the_browser()
    {
        var tab = await App.OpenAsync("/");
        var opened = App.OpenedLinks().Count;

        await WaitForTurnAsync();
        await tab.ClickAsync("#blank-outside");

        await Wait.UntilAsync(() => App.OpenedLinks().Count > opened, "the link reaching the browser");
        Assert.Equal("https://elsewhere.invalid/blank", App.OpenedLinks()[opened]);
        Assert.Equal("/", await tab.EvaluateAsync<string>("location.pathname"));
        Assert.Equal(AppProcess.ViewCount, await App.PageCountAsync());
    }

    [Theory]
    [InlineData("#mailto")]
    [InlineData("#settings")]
    public async Task A_link_to_another_application_starts_nothing(string link)
    {
        var tab = await App.OpenAsync("/");
        var opened = App.OpenedLinks().Count;
        var refused = Refusals();

        await tab.ClickAsync(link);

        await Wait.UntilAsync(() => Refusals() > refused, "the link being refused");
        Assert.Equal(opened, App.OpenedLinks().Count);
        Assert.Equal("/", await tab.EvaluateAsync<string>("location.pathname"));
        Assert.Equal(AppProcess.ViewCount, await App.PageCountAsync());
    }

    [Fact]
    public async Task A_link_without_https_leads_to_the_https_page()
    {
        var tab = await App.OpenAsync("/");
        var before = Site.Requests.Count;

        await tab.ClickAsync("#insecure");

        await tab.WaitForAsync("location.href === 'https://c411.org/insecure' && document.readyState === 'complete'");
        Assert.Contains(Site.Requests.Skip(before), request => request.Method == "GET" && request.Path == "/insecure");
    }

    [Theory]
    [InlineData("Geolocation", "new Promise(done => navigator.geolocation.getCurrentPosition(() => done('granted'), error => done('refused ' + error.code)))", "refused 1")]
    [InlineData("Notifications", "Notification.requestPermission()", "denied")]
    public async Task A_page_gets_none_of_the_permissions_it_asks_for(string kind, string request, string answer)
    {
        var tab = await App.OpenAsync("/");
        var denied = App.CountInLog(PermissionDenied + kind);

        var result = await tab.EvaluateAsync<string>(request, userGesture: true);

        Assert.Equal(answer, result);
        await App.WaitForLogAsync(PermissionDenied + kind, count: denied + 1);
    }

    [Fact]
    public async Task The_web_app_opens_a_page_of_the_site_in_the_tab()
    {
        var tab = await App.OpenAsync("/");
        var before = Site.Requests.Count;

        await App.Page.EvaluateAsync("App.openC411('https://c411.org/torrents?q=dune')");

        await tab.WaitForAsync("location.pathname + location.search === '/torrents?q=dune'");
        Assert.Contains(Site.Requests.Skip(before), request => request.Method == "GET" && request.Path == "/torrents?q=dune");
    }

    [Fact]
    public async Task The_search_of_the_web_app_gives_the_terms_to_the_site()
    {
        var tab = await App.OpenAsync("/");

        await App.Page.EvaluateAsync("App.c411Search(\"L'été de Dune (2021)\")");

        await tab.WaitForAsync("location.pathname === '/torrents'");
        Assert.Equal("l ete de dune 2021", await tab.EvaluateAsync<string>("new URLSearchParams(location.search).get('q')"));
        Assert.Equal("relevance", await tab.EvaluateAsync<string>("new URLSearchParams(location.search).get('sortBy')"));
    }

    [Fact]
    public async Task The_web_app_cannot_send_the_tab_to_another_site()
    {
        var tab = await App.OpenAsync("/");
        var refused = App.CountInLog(LinkRefused);

        await App.Page.EvaluateAsync("App.openC411('https://elsewhere.invalid/x')");

        await App.WaitForLogAsync(LinkRefused, count: refused + 1);
        Assert.Equal("/", await tab.EvaluateAsync<string>("location.pathname"));
    }

    // One link at a time reaches the browser (the app waits a second between two), and the tests that open one wait for their turn.
    private static Task WaitForTurnAsync() => Task.Delay(1_200);

    private int Refusals() => App.CountInLog(NavigationRefused) + App.CountInLog(LaunchRefused);
}
