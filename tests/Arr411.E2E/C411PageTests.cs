namespace Arr411.E2E;

/// <summary>The C411 tab: the page of the site, what the app puts in it, and what the page may do to the app.</summary>
[Trait("Category", "E2E")]
public sealed class C411PageTests(C411App fixture) : IClassFixture<C411App>
{
    private AppProcess App => fixture.App;

    [Fact]
    public async Task The_tab_shows_the_page_of_the_site()
    {
        var tab = await App.OpenAsync("/");

        Assert.Equal("https://c411.org/", await tab.EvaluateAsync<string>("location.href"));
        Assert.Equal("C411", await tab.EvaluateAsync<string>("document.title"));

        // And the picture of the window shows it (nothing else in the app has the colour of the page).
        var pixels = 0;
        for (var attempt = 0; attempt < 10 && pixels <= 5_000; attempt++)
        {
            using var picture = await App.CaptureAsync("c411-tab");
            pixels = WindowCapture.CountNear(picture, FakeC411.PageColor);
        }

        Assert.True(pixels > 5_000, $"The window shows {pixels} pixels of the page.");
    }

    [Fact]
    public async Task The_bridge_of_the_page_cannot_be_replaced_and_is_not_given_to_frames()
    {
        var tab = await App.OpenAsync("/");
        await tab.WaitForAsync("window.__qbtLoaded === true");

        var state = await tab.EvaluateAsync(
            """
            (() => {
              const descriptor = Object.getOwnPropertyDescriptor(window, 'QbtPage');
              const frame = document.getElementById('frame').contentWindow;
              return {
                defined: !!descriptor,
                writable: !!descriptor && descriptor.writable,
                configurable: !!descriptor && descriptor.configurable,
                enumerable: !!descriptor && descriptor.enumerable,
                frozen: Object.isFrozen(window.QbtPage),
                chrome: typeof window.__qbtChrome,
                frameBridge: typeof frame.QbtPage,
                frameChrome: typeof frame.__qbtChrome,
                frameLoaded: typeof frame.__qbtLoaded
              };
            })()
            """);

        Assert.True(state.GetProperty("defined").GetBoolean());
        Assert.False(state.GetProperty("writable").GetBoolean());
        Assert.False(state.GetProperty("configurable").GetBoolean());
        Assert.False(state.GetProperty("enumerable").GetBoolean());
        Assert.True(state.GetProperty("frozen").GetBoolean());
        Assert.Equal("object", state.GetProperty("chrome").GetString());

        // A frame of the page (the Cloudflare challenge, an ad...) gets neither the bridge nor the scripts that use it.
        Assert.Equal("undefined", state.GetProperty("frameBridge").GetString());
        Assert.Equal("undefined", state.GetProperty("frameChrome").GetString());
        Assert.Equal("undefined", state.GetProperty("frameLoaded").GetString());

        // The page itself cannot take the bridge over either (a strict script gets an error where a sloppy one is ignored).
        var attempts = await tab.EvaluateAsync(
            """
            (() => {
              'use strict';
              const outcome = {};
              const attempt = (name, action) => { try { action(); outcome[name] = 'allowed'; } catch (e) { outcome[name] = e.name; } };
              attempt('assign', () => { window.QbtPage = {}; });
              attempt('remove', () => { delete window.QbtPage; });
              attempt('define', () => { Object.defineProperty(window, 'QbtPage', { value: {} }); });
              attempt('patch', () => { window.QbtPage.send = () => {}; });
              return outcome;
            })()
            """);
        foreach (var name in new[] { "assign", "remove", "define", "patch" })
        {
            Assert.Equal("TypeError", attempts.GetProperty(name).GetString());
        }
    }

    [Fact]
    public async Task The_button_goes_next_to_the_download_button_of_a_torrent_page_and_nowhere_else()
    {
        var tab = await App.OpenAsync($"/torrents/{FakeC411.Hash}");

        Assert.Equal(1, await tab.WaitForOurButtonAsync());
        var button = await tab.EvaluateAsync(
            $$"""
            (() => {
              const ours = document.querySelector({{Js.Quote(C411Tab.OurButton)}});
              const native = document.getElementById('download');
              return {
                next: native.nextElementSibling === ours,
                title: ours.title,
                text: ours.textContent.trim(),
                id: ours.id,
                nativeText: native.textContent.trim()
              };
            })()
            """);
        Assert.True(button.GetProperty("next").GetBoolean());
        Assert.Equal("Envoyer à qBittorrent", button.GetProperty("title").GetString());
        Assert.Contains("qBittorrent", button.GetProperty("text").GetString());
        Assert.Equal("", button.GetProperty("id").GetString());
        Assert.Equal("Télécharger", button.GetProperty("nativeText").GetString());

        // No page of the site without a torrent gets one (the extension had the time to look: it is started by now).
        await App.OpenAsync("/");
        await tab.WaitForAsync("window.__qbtLoaded === true");
        await Task.Delay(1_500);
        Assert.Equal(0, await tab.CountAsync(C411Tab.OurButton));
    }

    [Fact]
    public async Task A_torrent_already_in_qbittorrent_is_shown_on_its_page()
    {
        fixture.Fake.Torrents =
            $$"""[{"hash":"{{FakeC411.Hash}}","name":"{{FakeC411.TorrentName}}","state":"downloading","progress":0.5,"category":"films"}]""";
        try
        {
            var tab = await App.OpenAsync($"/torrents/{FakeC411.Hash}");

            await tab.WaitForAsync("document.body.textContent.includes('qBittorrent : 50 % · téléchargement')");
            Assert.Contains(
                fixture.Fake.Requests,
                request => request.Path == $"/api/v2/torrents/info?hashes={FakeC411.Hash}" && request.Cookie == FakeServices.SessionCookie);
        }
        finally
        {
            fixture.Fake.Torrents = "[]";
        }
    }

    [Theory]
    [InlineData("listTorrents")]
    [InlineData("toggleTorrent")]
    [InlineData("somethingElse")]
    public async Task A_message_the_page_has_no_right_to_send_is_refused(string type)
    {
        var tab = await App.OpenAsync("/");
        await tab.WaitForAsync("window.__qbtLoaded === true");

        var answer = await tab.MessageAsync($$"""{ type: {{Js.Quote(type)}}, hash: {{Js.Quote(FakeC411.Hash)}}, pause: true }""");

        Assert.False(answer.GetProperty("ok").GetBoolean());
        Assert.Equal("Message refusé", answer.GetProperty("error").GetString());
        Assert.DoesNotContain(
            fixture.Fake.Requests,
            request => request.Path.Contains("/torrents/pause", StringComparison.Ordinal)
                || request.Path.Contains("/torrents/stop", StringComparison.Ordinal)
                || request.Path.Contains("/torrents/resume", StringComparison.Ordinal)
                || request.Path.Contains("/torrents/start", StringComparison.Ordinal));
    }
}
