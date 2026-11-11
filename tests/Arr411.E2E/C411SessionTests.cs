using System.Text.Json;

namespace Arr411.E2E;

/// <summary>What the C411 tab keeps from one run of the app to the next (the login of the user), and what it does when the site cannot be reached.</summary>
[Trait("Category", "E2E")]
public sealed class C411SessionTests
{
    [Fact]
    public async Task The_login_of_the_site_is_still_there_when_the_app_runs_again()
    {
        var dataDir = AppProcess.NewDataDir();
        try
        {
            await using var fake = await FakeServices.StartAsync();
            await using var site = await FakeC411.StartAsync();

            await using (var first = await AppProcess.StartAsync(C411App.Settings(fake), dataDir, site))
            {
                var tab = await first.OpenAsync("/login");

                // The login gave the cookie no expiry date, so that a browser forgets it when it closes: the app gives it one.
                var cookie = await WaitForKeptLoginAsync(tab);
                Assert.True(cookie.GetProperty("httpOnly").GetBoolean());
                Assert.True(cookie.GetProperty("secure").GetBoolean());
                var days = (cookie.GetProperty("expires").GetDouble() - DateTimeOffset.UtcNow.ToUnixTimeSeconds()) / 86_400;
                Assert.InRange(days, 29, 31);

                Assert.Equal(0, await first.CloseAsync());
            }

            await AppProcess.WaitForProfileReleaseAsync(dataDir);

            // The second run opens on the C411 tab by itself. What it knows of the user can only come from the disk.
            var before = site.Requests.Count;
            await using var second = await AppProcess.StartAsync(C411App.Settings(fake), dataDir, site);
            await Wait.UntilAsync(
                () => site.Requests.Skip(before).Any(request => request.Method == "GET" && request.Path == "/"),
                "the C411 tab asking the site for its home page");

            Assert.All(
                site.Requests.Skip(before).Where(request => request.Method == "GET" && request.Path == "/"),
                request => Assert.Contains(FakeC411.AuthCookie, request.Cookie ?? "", StringComparison.Ordinal));
        }
        finally
        {
            AppProcess.TryDeleteDirectory(dataDir);
        }
    }

    [Fact]
    public async Task The_app_starts_and_works_when_the_site_cannot_be_reached()
    {
        await using var fake = await FakeServices.StartAsync();
        await using var app = await AppProcess.StartAsync(C411App.Settings(fake));

        await app.WaitForLogAsync("C411 page not loaded", 30_000);

        // The rest of the app does not depend on the site: the other tabs work, and the window shows them.
        await app.Page.EvaluateAsync("App.goTab('radarr')");
        await app.Page.WaitForAsync("App.state.tab === 'radarr' && document.visibilityState === 'visible'");

        Assert.DoesNotContain(" ERROR ", app.ReadLog());
        Assert.Equal(0, await app.CloseAsync());
    }

    [Fact]
    public async Task The_tab_shows_the_site_again_once_it_can_be_reached()
    {
        await using var fake = await FakeServices.StartAsync();
        var port = Net.FreePort();
        await using var app = await AppProcess.StartAsync(C411App.Settings(fake), sitePort: port);
        await app.WaitForLogAsync("C411 page not loaded", 30_000);

        // The user is told, not left in front of the error page of the browser.
        Assert.Equal(0, await PixelsOfThePageAsync(app));

        await using var site = await FakeC411.StartAsync(port);
        await app.Page.EvaluateAsync("App.nat('reloadC411')");

        // The page of the browser that says the site is unreachable has the address of the site too: wait for the page of the site itself.
        var tab = await app.C411Async();
        await tab.WaitForAsync("document.title === 'C411'", 30_000);

        var pixels = 0;
        for (var attempt = 0; attempt < 10 && pixels <= 5_000; attempt++)
        {
            pixels = await PixelsOfThePageAsync(app);
            if (pixels <= 5_000) await Task.Delay(200);
        }

        Assert.True(pixels > 5_000, $"The window shows {pixels} pixels of the page.");
    }

    // How many pixels of the window have the colour of the pages of the stand-in (nothing else in the app has it).
    private static async Task<int> PixelsOfThePageAsync(AppProcess app)
    {
        using var picture = await app.CaptureAsync("c411-session");
        return WindowCapture.CountNear(picture, FakeC411.PageColor);
    }

    // The cookie of the login, once it has an expiry date.
    private static async Task<JsonElement> WaitForKeptLoginAsync(CdpClient tab)
    {
        var deadline = Environment.TickCount64 + 15_000;
        while (Environment.TickCount64 < deadline)
        {
            var cookies = (await tab.SendAsync("Network.getAllCookies")).GetProperty("cookies");
            foreach (var cookie in cookies.EnumerateArray())
            {
                if (cookie.GetProperty("name").GetString() == "auth" && !cookie.GetProperty("session").GetBoolean()) return cookie;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException("The cookie of the login never got an expiry date.");
    }
}
