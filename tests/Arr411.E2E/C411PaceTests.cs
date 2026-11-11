using Arr411.Core.Bridge;

namespace Arr411.E2E;

/// <summary>What the app does with a C411 page that asks it for too much at once: the page is answered "too many requests", then served again.</summary>
[Trait("Category", "E2E")]
public sealed class C411PaceTests
{
    private const string Categories = "{ type: 'getCategories' }";

    [Fact]
    public async Task A_page_that_asks_in_a_burst_is_stopped_and_is_served_again_once_it_has_waited()
    {
        await using var fake = await FakeServices.StartAsync();
        await using var site = await FakeC411.StartAsync();

        // The other tests shorten the window of the limits (see AppProcess.StartAsync); this one needs it long enough to catch a burst.
        await using var app = await AppProcess.StartAsync(
            C411App.Settings(fake),
            site: site,
            environment: new Dictionary<string, string> { ["ARR411_PAGE_RATE_WINDOW"] = "2000" });
        var tab = await app.OpenAsync("/");
        await tab.WaitForAsync("window.__qbtLoaded === true");

        var burst = PageBridge.MaxRequestsPerWindow + 10;
        var answers = (await tab.EvaluateAsync(
            $"Promise.all(Array.from({{ length: {burst} }}, () => window.__qbtChrome.runtime.sendMessage({Categories})))")).EnumerateArray().ToArray();

        Assert.Equal(burst, answers.Length);
        var refused = answers.Where(answer => !answer.GetProperty("ok").GetBoolean()).ToArray();
        Assert.True(refused.Length >= burst - PageBridge.MaxRequestsPerWindow, $"Only {refused.Length} messages of the burst were refused.");
        Assert.True(refused.Length < burst, "The page was not served at all.");
        Assert.All(refused, answer => Assert.Contains("Trop de requêtes", answer.GetProperty("error").GetString(), StringComparison.Ordinal));

        // Its messages get old: after the window, the page is served as before.
        var served = false;
        for (var attempt = 0; attempt < 50 && !served; attempt++)
        {
            served = (await tab.MessageAsync(Categories)).GetProperty("ok").GetBoolean();
            if (!served) await Task.Delay(200);
        }

        Assert.True(served, "The page was never served again.");
        Assert.DoesNotContain(" ERROR ", app.ReadLog());
    }
}
