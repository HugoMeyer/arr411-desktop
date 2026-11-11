using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace Arr411.E2E;

/// <summary>The engine over time: starting clean, staying awake, coming back after a crash, and telling the user when a download is done.</summary>
[Trait("Category", "E2E")]
public sealed class EngineLifecycleTests
{
    private const string Hash = "0123456789abcdef0123456789abcdef01234567";
    private const string Name = "Film.2049.1080p-GROUP";
    private const string InfoPath = "/api/v2/torrents/info?hashes=" + Hash;
    private const int SwMinimize = 6;
    private const int SwRestore = 9;

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr window, int command);

    // An app that watches one download of the fake qBittorrent, which is at some progress.
    private static Task<AppProcess> StartWatchingAsync(FakeServices fake, double progress)
    {
        Watch(fake, progress);
        return AppProcess.StartAsync(new JsonObject
        {
            ["qbUrl"] = fake.BaseUrl,
            ["username"] = FakeServices.QbUser,
            ["password"] = FakeServices.QbPassword,
            ["startTab"] = "radarr",
            ["tracked"] = new JsonObject
            {
                [Hash] = new JsonObject { ["name"] = Name + ".torrent", ["added"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() },
            },
        });
    }

    // What the fake qBittorrent says about the download from now on.
    private static void Watch(FakeServices fake, double progress) =>
        fake.Torrents = string.Create(
            CultureInfo.InvariantCulture,
            $$"""[{"hash":"{{Hash}}","name":"{{Name}}","progress":{{progress}},"state":"downloading","category":"films"}]""");

    // The engine looks at the downloads every 30 seconds: make it every second.
    private static async Task HurryUpAsync(AppProcess app)
    {
        var engine = await app.EngineAsync();
        await engine.SendAsync(
            "Page.addScriptToEvaluateOnNewDocument",
            new { source = "(() => { const every = window.setInterval; window.setInterval = (run, ms, ...rest) => every(run, ms === 30000 ? 1000 : ms, ...rest); })()" });
        await app.ReloadEngineAsync();
    }

    private static int InfoRequests(FakeServices fake, int status) =>
        fake.Requests.Count(request => request.Path == InfoPath && request.Status == status);

    [Fact]
    public async Task The_engine_starts_without_a_problem()
    {
        await using var app = await AppProcess.StartAsync();
        var engine = await app.EngineAsync();

        // It was loaded before this connection: load it again to see everything it does.
        await app.ReloadEngineAsync();
        await Task.Delay(1_000);

        Assert.Empty(engine.Problems());
        var log = app.ReadLog();
        Assert.DoesNotContain(" ERROR ", log);
        Assert.DoesNotContain(" WARN ", log);
    }

    [Fact]
    public async Task A_finished_download_raises_one_notification_and_is_no_longer_watched()
    {
        await using var fake = await FakeServices.StartAsync();
        await using var app = await StartWatchingAsync(fake, progress: 1);

        // The web app signed in when it started, and the engine shares its session: that session ends just when the engine asks.
        fake.ExpireSessionOnNextTorrentList();
        await HurryUpAsync(app);
        await app.WaitForLogAsync("Notification shown", 30_000);
        await app.WaitForStoredConfigAsync(stored => stored["tracked"] is JsonObject { Count: 0 }, "no download left to watch");

        // It asked as the extension does: refused, it logged in and asked again.
        var requests = fake.Requests.ToList();
        var refused = requests.FindIndex(request => request.Path == InfoPath && request.Status == 403);
        Assert.True(refused >= 0, "The engine was not refused.");
        var loggedIn = requests.FindIndex(refused, request => request.Method == "POST" && request.Path == "/api/v2/auth/login" && request.Status == 200);
        Assert.True(loggedIn > refused, "The engine did not log in after it was refused.");
        Assert.Contains(requests.Skip(loggedIn), request => request.Path == InfoPath && request.Cookie == FakeServices.SessionCookie && request.Status == 200);

        // Nothing more once it is done.
        await Task.Delay(2_500);
        Assert.Equal(1, app.CountInLog("Notification shown"));

        // The log says that a notification was shown, not what it said, and holds no secret.
        var log = app.ReadLog();
        Assert.DoesNotContain(Name, log);
        Assert.DoesNotContain(FakeServices.QbPassword, log);
        Assert.DoesNotContain("127.0.0.1", log);
        Assert.DoesNotContain(" ERROR ", log);
    }

    [Fact]
    public async Task A_download_that_finishes_while_the_window_is_hidden_still_raises_a_notification()
    {
        await using var fake = await FakeServices.StartAsync();
        await using var app = await StartWatchingAsync(fake, progress: 0.5);
        var window = await app.WindowAsync();

        // The user closes the window: the app goes on in the notification area, which is where a notification matters.
        app.CloseWindow();
        await Wait.UntilAsync(() => !Win32.IsVisible(window), "the window leaving the screen");
        await HurryUpAsync(app);
        await Wait.UntilAsync(() => InfoRequests(fake, 200) >= 2, "the engine looking at the download");
        Assert.Equal(0, app.CountInLog("Notification shown"));

        Watch(fake, progress: 1);

        await app.WaitForLogAsync("Notification shown", 30_000);
        await app.WaitForStoredConfigAsync(stored => stored["tracked"] is JsonObject { Count: 0 }, "no download left to watch");
        Assert.False(Win32.IsVisible(window));
        Assert.DoesNotContain(" ERROR ", app.ReadLog());
    }

    [Fact]
    public async Task A_download_still_running_raises_nothing_and_stays_watched()
    {
        await using var fake = await FakeServices.StartAsync();
        await using var app = await StartWatchingAsync(fake, progress: 0.5);

        await HurryUpAsync(app);
        await Wait.UntilAsync(() => InfoRequests(fake, 200) >= 3, "the engine looked at the download three times");

        Assert.Equal(0, app.CountInLog("Notification shown"));
        var tracked = app.ReadStoredConfig()?["tracked"] as JsonObject;
        Assert.NotNull(tracked?[Hash]);
    }

    [Fact]
    public async Task The_engine_asks_nothing_while_no_download_is_watched()
    {
        await using var fake = await FakeServices.StartAsync();
        await using var app = await AppProcess.StartAsync(new JsonObject
        {
            ["qbUrl"] = fake.BaseUrl,
            ["username"] = FakeServices.QbUser,
            ["password"] = FakeServices.QbPassword,
            ["startTab"] = "radarr",
        });

        await HurryUpAsync(app);
        await Task.Delay(3_500);

        // The web app has its own requests to qBittorrent (its list of downloads); the status of a watched download is the engine's.
        Assert.DoesNotContain(fake.Requests, request => request.Path.StartsWith("/api/v2/torrents/info", StringComparison.Ordinal));
        Assert.Equal(0, app.CountInLog("Notification shown"));
    }

    [Fact]
    public async Task The_engine_stays_visible_when_the_window_is_minimized()
    {
        await using var app = await AppProcess.StartAsync();
        var engine = await app.EngineAsync();
        var window = await app.WindowAsync();

        ShowWindow(window, SwMinimize);
        try
        {
            await Task.Delay(1_500);

            // A page that is hidden is a page the browser puts to sleep.
            Assert.Equal("visible", await engine.EvaluateAsync<string>("document.visibilityState"));
            Assert.False(await engine.EvaluateAsync<bool>("document.hidden"));
        }
        finally
        {
            ShowWindow(window, SwRestore);
        }
    }

    [Fact]
    public async Task The_engine_comes_back_after_its_view_crashes()
    {
        await using var app = await AppProcess.StartAsync();
        var engine = await app.EngineAsync();
        await engine.EvaluateAsync("window.__beforeCrash = true");

        try
        {
            await engine.SendAsync("Page.crash", timeoutMs: 5_000);
        }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException)
        {
            // The page does not answer a command that kills it.
        }

        await app.WaitForLogAsync("Web view process failed in the engine");

        var back = await app.EngineAsync(fresh: true);
        Assert.False(await back.EvaluateAsync<bool>("!!window.__beforeCrash"));
        Assert.Equal("https://engine.arr411.local/engine.html", await back.EvaluateAsync<string>("location.href"));

        // The web app did not notice.
        Assert.Equal("settings", await app.Page.EvaluateAsync<string>("App.state.tab"));
    }
}
