using System.Text.Json;

namespace Arr411.E2E;

/// <summary>What the web app must not be able to do, tried from inside the page the way hostile content would.</summary>
[Trait("Category", "E2E")]
public sealed class SecurityTests(ConfiguredApp fixture) : IClassFixture<ConfiguredApp>, IAsyncLifetime
{
    private AppProcess App => fixture.App;

    private CdpClient Page => fixture.App.Page;

    private FakeServices Fake => fixture.Fake;

    // The page keeps a record of what its security policy blocked, as "directive blocked-address".
    public Task InitializeAsync() => Page.EvaluateAsync(
        """
        if (!window.__violations) {
          window.__violations = [];
          document.addEventListener('securitypolicyviolation', e => window.__violations.push(e.effectiveDirective + ' ' + e.blockedURI));
        }
        """);

    public Task DisposeAsync() => Task.CompletedTask;

    private Task WaitForViolationAsync(string directive, string blocked) => Page.WaitForAsync(
        $"window.__violations.some(v => v.startsWith({Js.Quote(directive)}) && v.endsWith({Js.Quote(blocked)}))");

    private void AssertNothingReached(string path) =>
        Assert.DoesNotContain(Fake.Requests, request => request.Path.StartsWith(path, StringComparison.Ordinal));

    [Fact]
    public async Task A_script_from_another_origin_does_not_run()
    {
        var outcome = await Page.EvaluateAsync<string>(
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
    public async Task An_inline_script_does_not_run()
    {
        var outcome = await Page.EvaluateAsync<string>(
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
    public async Task Text_cannot_be_turned_into_code()
    {
        // The functions are made here but called from a timer: the debugger's own evaluation is exempt from the policy, later code of the page is not.
        var outcome = await Page.EvaluateAsync<string[]>(
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

    [Fact]
    public async Task A_request_to_another_origin_is_refused()
    {
        var outcome = await Page.EvaluateAsync<string>(
            $$"""fetch({{Js.Quote(Fake.BaseUrl + "/ping/fetch")}}).then(() => 'answered', () => 'refused')""");

        Assert.Equal("refused", outcome);
        await WaitForViolationAsync("connect-src", "/ping/fetch");
        AssertNothingReached("/ping/fetch");
    }

    [Fact]
    public async Task Images_and_frames_from_another_origin_are_refused()
    {
        await Page.EvaluateAsync(
            $$"""
            (() => {
              new Image().src = {{Js.Quote(Fake.BaseUrl + "/ping/image")}};
              const frame = document.createElement('iframe');
              frame.src = {{Js.Quote(Fake.BaseUrl + "/ping/frame")}};
              document.body.appendChild(frame);
              setTimeout(() => frame.remove(), 1000);
            })()
            """);

        await WaitForViolationAsync("img-src", "/ping/image");
        // The browser reports only the origin of a frame it blocked.
        await WaitForViolationAsync("frame-src", Fake.BaseUrl);
        AssertNothingReached("/ping/image");
        AssertNothingReached("/ping/frame");
    }

    [Fact]
    public async Task A_form_cannot_send_anything_elsewhere()
    {
        await Page.EvaluateAsync(
            $$"""
            (() => {
              const form = document.createElement('form');
              form.method = 'post';
              form.action = {{Js.Quote(Fake.BaseUrl + "/ping/form")}};
              document.body.appendChild(form);
              form.submit();
              form.remove();
            })()
            """);

        await WaitForViolationAsync("form-action", "/ping/form");
        AssertNothingReached("/ping/form");
    }

    // Navigations are not covered by the page policy: the window itself has to refuse them, and the request must not even leave.
    [Theory]
    [InlineData("location.assign(url)")]
    [InlineData("document.head.append(Object.assign(document.createElement('meta'), { httpEquiv: 'refresh', content: '0;url=' + url }))")]
    public async Task The_page_cannot_navigate_away(string attempt)
    {
        const string refused = "A navigation of the web app was refused";
        var path = "/ping/navigate/" + Guid.NewGuid().ToString("N")[..8];
        var before = App.CountInLog(refused);

        await Page.EvaluateAsync($$"""(url => { {{attempt}} })({{Js.Quote(Fake.BaseUrl + path)}})""");
        await App.WaitForLogAsync(refused, count: before + 1);
        await Task.Delay(500);

        Assert.Equal("https://arr411.local/app/index.html", await Page.EvaluateAsync<string>("location.href"));
        AssertNothingReached(path);
    }

    [Fact]
    public async Task A_popup_cannot_be_opened()
    {
        await Page.EvaluateAsync($$"""window.open({{Js.Quote(Fake.BaseUrl + "/ping/popup")}})""");
        await Task.Delay(1_000);

        Assert.Equal(AppProcess.ViewCount, await App.PageCountAsync());
        AssertNothingReached("/ping/popup");
    }

    [Fact]
    public async Task A_download_is_refused()
    {
        var name = "arr411-e2e-" + Guid.NewGuid().ToString("N")[..8] + ".txt";

        await Page.EvaluateAsync(
            $$"""
            (() => {
              const link = document.createElement('a');
              link.href = URL.createObjectURL(new Blob(['x']));
              link.download = {{Js.Quote(name)}};
              document.body.appendChild(link);
              link.click();
              link.remove();
            })()
            """);
        await App.WaitForLogAsync("A download of the web app was refused");
        await Task.Delay(500);

        Assert.False(File.Exists(Path.Combine(App.DownloadsDir, name)));
        Assert.False(File.Exists(Path.Combine(KnownFolders.DownloadsFolder(), name)));
    }

    [Fact]
    public async Task A_permission_is_denied()
    {
        var code = await Page.EvaluateAsync<int>(
            "new Promise(resolve => navigator.geolocation.getCurrentPosition(() => resolve(0), e => resolve(e.code)))");

        Assert.Equal(1, code);
        await App.WaitForLogAsync("A permission request of the web app was denied: Geolocation");
    }

    [Fact]
    public async Task Unknown_paths_and_methods_are_refused()
    {
        var statuses = await Page.EvaluateAsync<int[]>(
            """
            Promise.all([
              fetch('/nope').then(r => r.status),
              fetch('/app/nope.js').then(r => r.status),
              fetch('/app/index.html', { method: 'POST', body: 'x' }).then(r => r.status),
              fetch('/native/state', { method: 'DELETE' }).then(r => r.status),
            ])
            """);

        int[] expected = [404, 404, 405, 405];
        Assert.Equal(expected, statuses);
    }

    [Fact]
    public async Task Pages_carry_a_strict_policy()
    {
        var headers = await Page.EvaluateAsync<JsonElement>(
            """
            fetch('/app/index.html').then(r => ({
              policy: r.headers.get('content-security-policy'),
              sniffing: r.headers.get('x-content-type-options'),
            }))
            """);

        Assert.StartsWith("default-src 'none'", headers.GetProperty("policy").GetString());
        Assert.Equal("nosniff", headers.GetProperty("sniffing").GetString());
    }
}
