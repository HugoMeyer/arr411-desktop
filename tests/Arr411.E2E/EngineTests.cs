using System.Text.Json;

namespace Arr411.E2E;

/// <summary>The hidden engine page: what it gets from the window, and what it must not be able to do.</summary>
[Trait("Category", "E2E")]
public sealed class EngineTests(ConfiguredApp fixture) : IClassFixture<ConfiguredApp>, IAsyncLifetime
{
    private AppProcess App => fixture.App;

    private FakeServices Fake => fixture.Fake;

    private CdpClient Engine { get; set; } = null!;

    public async Task InitializeAsync()
    {
        Engine = await App.EngineAsync();

        // The page keeps a record of what its security policy blocked, as "directive blocked-address".
        await Engine.EvaluateAsync(
            """
            if (!window.__violations) {
              window.__violations = [];
              document.addEventListener('securitypolicyviolation', e => window.__violations.push(e.effectiveDirective + ' ' + e.blockedURI));
            }
            """);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private Task WaitForViolationAsync(string directive, string blocked) => Engine.WaitForAsync(
        $"window.__violations.some(v => v.startsWith({Js.Quote(directive)}) && v.endsWith({Js.Quote(blocked)}))");

    private void AssertNothingReached(string path) =>
        Assert.DoesNotContain(Fake.Requests, request => request.Path.StartsWith(path, StringComparison.Ordinal));

    [Fact]
    public async Task The_engine_runs_in_a_page_of_its_own()
    {
        Assert.Equal("https://engine.arr411.local/engine.html", await Engine.EvaluateAsync<string>("location.href"));
        Assert.Equal("https://engine.arr411.local", await Engine.EvaluateAsync<string>("location.origin"));
        Assert.Equal("https://arr411.local/app/index.html", await App.Page.EvaluateAsync<string>("location.href"));
    }

    [Fact]
    public async Task The_engine_has_the_extension_api_and_a_bridge_of_its_own()
    {
        Assert.True(await Engine.EvaluateAsync<bool>(
            "typeof chrome.storage.local.get === 'function' && typeof chrome.alarms.create === 'function' && typeof chrome.notifications.create === 'function'"));

        // Not the bridge of the web app, and nothing more than what the engine needs.
        Assert.Equal("undefined", await Engine.EvaluateAsync<string>("typeof Native"));
        string[] expected = ["emit", "http", "notify", "openUrl", "ready", "reply", "storageGet", "storageSet"];
        Assert.Equal(expected, await Engine.EvaluateAsync<string[]>("Object.keys(QbtEngine).sort()"));
    }

    [Fact]
    public async Task The_engine_bridge_cannot_be_replaced_or_patched()
    {
        var outcome = await Engine.EvaluateAsync<bool[]>(
            """
            (() => {
              const original = window.QbtEngine;
              const attempt = run => { try { run(); } catch (e) { /* strict or not, what matters is below */ } };
              attempt(() => { window.QbtEngine = {}; });
              attempt(() => { window.QbtEngine.storageGet = () => '{}'; });
              attempt(() => { delete window.QbtEngine; });
              attempt(() => Object.defineProperty(window, 'QbtEngine', { value: {} }));
              return [window.QbtEngine === original, Object.isFrozen(window.QbtEngine), QbtEngine.storageGet() !== '{}'];
            })()
            """);

        bool[] expected = [true, true, true];
        Assert.Equal(expected, outcome);
    }

    [Fact]
    public async Task The_engine_reads_the_settings_of_the_window()
    {
        var stored = await Engine.EvaluateAsync<JsonElement>("chrome.storage.local.get(['radarrUrl', 'radarrKey'])");

        Assert.Equal(Fake.BaseUrl, stored.GetProperty("radarrUrl").GetString());
        Assert.Equal(FakeServices.ApiKey, stored.GetProperty("radarrKey").GetString());
    }

    [Fact]
    public async Task A_setting_saved_by_the_web_app_reaches_the_engine()
    {
        var value = "films-" + Guid.NewGuid().ToString("N")[..8];

        await App.Page.EvaluateAsync($$"""App.saveCfg({ lastCategory: {{Js.Quote(value)}} })""");

        await Engine.WaitForAsync($"JSON.parse(QbtEngine.storageGet()).lastCategory === {Js.Quote(value)}");
    }

    [Fact]
    public async Task A_setting_saved_by_the_engine_reaches_the_disk_and_the_web_app()
    {
        var value = "note-" + Guid.NewGuid().ToString("N")[..8];

        await Engine.EvaluateAsync($$"""chrome.storage.local.set({ engineNote: {{Js.Quote(value)}} })""");

        await App.WaitForStoredConfigAsync(stored => stored["engineNote"]?.GetValue<string>() == value, "engineNote");
        await App.Page.WaitForAsync($"JSON.parse(Native.config()).engineNote === {Js.Quote(value)}");
    }

    [Fact]
    public async Task The_engine_reaches_servers_through_the_window_as_a_native_client()
    {
        var echo = await Engine.EvaluateAsync<JsonElement>($$"""fetch({{Js.Quote(Fake.BaseUrl + "/echo")}}).then(r => r.json())""");

        Assert.Equal(Fake.BaseUrl, echo.GetProperty("origin").GetString());
        Assert.Equal(Fake.BaseUrl + "/", echo.GetProperty("referer").GetString());
        Assert.Equal("Arr411-Desktop/" + AppProcess.ExpectedVersion, echo.GetProperty("userAgent").GetString());
    }

    [Fact]
    public async Task A_form_goes_out_of_the_engine_as_the_extension_sends_it()
    {
        var echo = await Engine.EvaluateAsync<JsonElement>(
            $$"""
            fetch({{Js.Quote(Fake.BaseUrl + "/echo")}}, { method: 'POST', body: new URLSearchParams({ username: 'admin', note: 'caf\u00e9' }) }).then(r => r.json())
            """);

        Assert.Equal("POST", echo.GetProperty("method").GetString());
        Assert.Equal("application/x-www-form-urlencoded;charset=UTF-8", echo.GetProperty("contentType").GetString());
        Assert.Equal("username=admin&note=caf%C3%A9", echo.GetProperty("body").GetString());
    }

    [Fact]
    public async Task A_server_that_cannot_be_reached_is_a_network_error_for_the_engine()
    {
        var outcome = await Engine.EvaluateAsync<string>($$"""fetch('http://127.0.0.1:{{Net.FreePort()}}/').then(() => 'answered', e => e.name)""");

        Assert.Equal("TypeError", outcome);
    }

    [Fact]
    public async Task The_engine_cannot_reach_another_origin_by_itself()
    {
        // fetch is the window's own implementation here: the browser's request object is the way to try the browser itself.
        var outcome = await Engine.EvaluateAsync<string>(
            $$"""
            new Promise(resolve => {
              const request = new XMLHttpRequest();
              request.open('GET', {{Js.Quote(Fake.BaseUrl + "/ping/xhr")}});
              request.onload = () => resolve('answered');
              request.onerror = () => resolve('refused');
              request.send();
            })
            """);

        Assert.Equal("refused", outcome);
        await WaitForViolationAsync("connect-src", "/ping/xhr");
        AssertNothingReached("/ping/xhr");
    }

    [Fact]
    public async Task A_script_from_another_origin_does_not_run_in_the_engine()
    {
        var outcome = await Engine.EvaluateAsync<string>(
            $$"""
            new Promise(resolve => {
              const script = document.createElement('script');
              script.src = {{Js.Quote(Fake.BaseUrl + "/ping/script")}};
              script.onload = () => resolve('loaded');
              script.onerror = () => resolve('blocked');
              document.head.appendChild(script);
            })
            """);

        Assert.Equal("blocked", outcome);
        await WaitForViolationAsync("script-src", "/ping/script");
        AssertNothingReached("/ping/script");
    }

    [Fact]
    public async Task An_inline_script_does_not_run_in_the_engine()
    {
        var outcome = await Engine.EvaluateAsync<string>(
            """
            (() => {
              window.__inline = 'not run';
              const script = document.createElement('script');
              script.textContent = "window.__inline = 'run'";
              document.head.appendChild(script);
              return window.__inline;
            })()
            """);

        Assert.Equal("not run", outcome);
    }

    [Fact]
    public async Task Text_cannot_be_turned_into_code_in_the_engine()
    {
        // The functions are made here but called from a timer: the debugger's own evaluation is exempt from the policy, later code of the page is not.
        var outcome = await Engine.EvaluateAsync<string[]>(
            """
            new Promise(resolve => setTimeout(() => {
              const attempt = run => { try { return 'ran ' + run(); } catch (e) { return 'blocked'; } };
              resolve([attempt(() => new Function('return 1 + 1')()), attempt(() => eval('1 + 1'))]);
            }, 0))
            """);

        string[] expected = ["blocked", "blocked"];
        Assert.Equal(expected, outcome);
        await WaitForViolationAsync("script-src", "eval");
    }

    [Theory]
    [InlineData("location.assign(url)")]
    [InlineData("document.head.append(Object.assign(document.createElement('meta'), { httpEquiv: 'refresh', content: '0;url=' + url }))")]
    public async Task The_engine_cannot_navigate_away(string attempt)
    {
        const string refused = "A navigation of the engine was refused";
        var path = "/ping/navigate/" + Guid.NewGuid().ToString("N")[..8];
        var before = App.CountInLog(refused);

        await Engine.EvaluateAsync($$"""(url => { {{attempt}} })({{Js.Quote(Fake.BaseUrl + path)}})""");
        await App.WaitForLogAsync(refused, count: before + 1);
        await Task.Delay(500);

        Assert.Equal("https://engine.arr411.local/engine.html", await Engine.EvaluateAsync<string>("location.href"));
        AssertNothingReached(path);
    }

    [Fact]
    public async Task The_engine_cannot_open_a_window()
    {
        const string refused = "A new window of the engine was refused";
        var before = App.CountInLog(refused);

        await Engine.EvaluateAsync($$"""window.open({{Js.Quote(Fake.BaseUrl + "/ping/popup")}})""");
        await App.WaitForLogAsync(refused, count: before + 1);
        await Task.Delay(500);

        Assert.Equal(AppProcess.ViewCount, await App.PageCountAsync());
        AssertNothingReached("/ping/popup");
    }

    [Fact]
    public async Task The_engine_cannot_reach_the_web_app()
    {
        var outcome = await Engine.EvaluateAsync<string>(
            """
            new Promise(resolve => {
              const request = new XMLHttpRequest();
              request.open('GET', 'https://arr411.local/app/index.html');
              request.onload = () => resolve('answered');
              request.onerror = () => resolve('refused');
              request.send();
            })
            """);

        Assert.Equal("refused", outcome);
        await WaitForViolationAsync("connect-src", "https://arr411.local/app/index.html");
    }

    // The state of the engine holds every setting, the secrets included.
    [Fact]
    public async Task The_web_app_cannot_reach_the_engine()
    {
        var outcome = await App.Page.EvaluateAsync<string>("fetch('https://engine.arr411.local/native/state').then(() => 'answered', () => 'refused')");

        Assert.Equal("refused", outcome);
    }
}
