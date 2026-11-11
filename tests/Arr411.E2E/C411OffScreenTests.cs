using System.Text.Json;

namespace Arr411.E2E;

/// <summary>What the C411 page may still ask of the app once nobody can see it: its tab is not the one on screen.</summary>
[Trait("Category", "E2E")]
public sealed class C411OffScreenTests
{
    private const string Categories = "{ type: 'getCategories' }";

    [Fact]
    public async Task A_page_nobody_sees_is_no_longer_served_and_is_again_when_it_comes_back()
    {
        await using var fake = await FakeServices.StartAsync();
        await using var site = await FakeC411.StartAsync();

        // No grace: the page is refused as soon as its tab is left (the real app gives it a minute, so that what it was doing is not lost).
        await using var app = await AppProcess.StartAsync(
            C411App.Settings(fake),
            site: site,
            environment: new Dictionary<string, string> { ["ARR411_PAGE_GRACE"] = "0" });
        var tab = await app.OpenAsync("/");
        await tab.WaitForAsync("window.__qbtLoaded === true");
        Assert.True((await tab.MessageAsync(Categories)).GetProperty("ok").GetBoolean());

        await app.Page.EvaluateAsync("App.goTab('radarr')");
        await app.Page.WaitForAsync("App.state.tab === 'radarr' && document.visibilityState === 'visible'");
        var refused = await AnswerAsync(tab, answer => !answer.GetProperty("ok").GetBoolean());
        Assert.False(refused.GetProperty("ok").GetBoolean());
        Assert.Contains("masqué", refused.GetProperty("error").GetString(), StringComparison.Ordinal);

        await app.Page.EvaluateAsync("App.goTab('c411')");
        await tab.WaitForAsync("document.visibilityState === 'visible'");
        var served = await AnswerAsync(tab, answer => answer.GetProperty("ok").GetBoolean());
        Assert.True(served.GetProperty("ok").GetBoolean());
        Assert.DoesNotContain(" ERROR ", app.ReadLog());
    }

    [Fact]
    public async Task A_page_the_user_has_just_left_is_still_served()
    {
        await using var fake = await FakeServices.StartAsync();
        await using var site = await FakeC411.StartAsync();
        await using var app = await AppProcess.StartAsync(C411App.Settings(fake), site: site);
        var tab = await app.OpenAsync("/");
        await tab.WaitForAsync("window.__qbtLoaded === true");

        // The user sends a torrent and goes to look at the other tabs: what the page is in the middle of must not be lost.
        await app.Page.EvaluateAsync("App.goTab('radarr')");
        await app.Page.WaitForAsync("App.state.tab === 'radarr' && document.visibilityState === 'visible'");
        await tab.WaitForAsync("document.visibilityState === 'hidden'");

        Assert.True((await tab.MessageAsync(Categories)).GetProperty("ok").GetBoolean());
    }

    // The tab is told about its visibility a moment after it changes: ask until the answer is the one that goes with it.
    private static async Task<JsonElement> AnswerAsync(CdpClient tab, Func<JsonElement, bool> wanted)
    {
        var deadline = Environment.TickCount64 + 10_000;
        while (true)
        {
            var answer = await tab.MessageAsync(Categories);
            if (wanted(answer) || Environment.TickCount64 > deadline) return answer;

            await Task.Delay(200);
        }
    }
}
