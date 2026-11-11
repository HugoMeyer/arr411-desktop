using System.Text.Json;

namespace Arr411.E2E;

/// <summary>The app starts, shows its web app and gives it the native bridge.</summary>
[Trait("Category", "E2E")]
public sealed class BootTests(FirstRunApp fixture) : IClassFixture<FirstRunApp>
{
    private AppProcess App => fixture.App;

    [Fact]
    public async Task The_web_app_is_served_from_the_virtual_host()
    {
        Assert.Equal("https://arr411.local/app/index.html", await App.Page.EvaluateAsync<string>("location.href"));
        Assert.Equal("Arr411", await App.Page.EvaluateAsync<string>("document.title"));
    }

    [Fact]
    public async Task A_first_run_opens_the_settings()
    {
        Assert.Equal("settings", await App.Page.EvaluateAsync<string>("App.state.tab"));
        Assert.Equal("settings", await App.Page.EvaluateAsync<string>("Native.currentTab()"));
    }

    [Fact]
    public async Task The_native_bridge_has_the_methods_the_web_app_knows_and_no_other()
    {
        string[] known =
        [
            "ready", "http", "take", "config", "saveConfig", "currentTab", "appVersion", "selectTab",
            "openC411", "reloadC411", "openUrl", "toast", "setBadge", "copy", "haptic",
        ];

        var actual = await App.Page.EvaluateAsync<string[]>("Object.keys(Native).sort()");

        Assert.Equal(known.Order(StringComparer.Ordinal).ToArray(), actual);
    }

    [Fact]
    public async Task The_native_bridge_cannot_be_replaced_or_patched()
    {
        var result = await App.Page.EvaluateAsync<JsonElement>(
            """
            (() => {
              const before = window.Native;
              const original = Native.http;
              try { window.Native = {}; } catch (e) { /* refused */ }
              try { delete window.Native; } catch (e) { /* refused */ }
              try { Native.http = () => 'hijacked'; } catch (e) { /* refused */ }
              try { Object.defineProperty(window, 'Native', { value: {} }); } catch (e) { /* refused */ }
              return {
                same: window.Native === before,
                frozen: Object.isFrozen(window.Native),
                untouched: Native.http === original,
              };
            })()
            """);

        Assert.True(result.GetProperty("same").GetBoolean());
        Assert.True(result.GetProperty("frozen").GetBoolean());
        Assert.True(result.GetProperty("untouched").GetBoolean());
    }

    [Fact]
    public async Task The_bridge_reports_the_version_of_the_app()
    {
        Assert.Equal(AppProcess.ExpectedVersion, await App.Page.EvaluateAsync<string>("Native.appVersion()"));
    }

    [Fact]
    public async Task The_page_loads_without_errors()
    {
        await Task.Delay(500);

        Assert.Empty(App.Page.Problems());
    }

    [Fact]
    public async Task The_window_paints_the_page()
    {
        // The first paint comes a moment after the page is ready.
        var colors = 0;
        for (var attempt = 0; attempt < 10 && colors <= 30; attempt++)
        {
            using var picture = await App.CaptureAsync("first-run");
            colors = WindowCapture.DistinctColors(picture);
        }

        // A blank window has one colour; text and controls on a dark background have dozens.
        Assert.True(colors > 30, $"The window shows {colors} colours.");
    }

    [Fact]
    public void The_log_tells_which_version_started()
    {
        Assert.Contains("Arr411 Desktop " + AppProcess.ExpectedVersion + " starting", App.ReadLog());
    }
}
