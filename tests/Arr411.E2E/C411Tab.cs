using System.Text.Json;

namespace Arr411.E2E;

/// <summary>What the tests do in the C411 tab: open a page of the stand-in for the site, click what a user would click, look at what the extension put there.</summary>
internal static class C411Tab
{
    /// <summary>The button the extension puts next to the download button of C411.</summary>
    public const string OurButton = "button[data-qbt-injected=\"ours\"]";

    /// <summary>The toasts of the extension: it says what it did there.</summary>
    public const string Toasts = "#qbt-toasts";

    /// <summary>Opens a page of the site in the tab, waits until it is loaded, and returns the tab. Nothing an earlier test armed is left.</summary>
    public static async Task<CdpClient> OpenAsync(this AppProcess app, string path)
    {
        // Whatever the window shows, the tab of C411 is what the test looks at: a page that is not shown does not get the mouse.
        await app.Page.EvaluateAsync("App.goTab('c411')");
        var tab = await app.C411Async();
        await tab.EvaluateAsync("window.__beforeNavigation = true");
        await tab.SendAsync("Page.navigate", new { url = "https://c411.org" + path });
        await tab.WaitForAsync(
            $"!window.__beforeNavigation && document.readyState === 'complete' && location.pathname + location.search === {Js.Quote(path)}",
            30_000);

        // The engine stays armed for a while after a send that went wrong.
        await tab.EvaluateAsync("window.__qbtChrome ? window.__qbtChrome.runtime.sendMessage({ type: 'disarm' }) : null");
        return tab;
    }

    /// <summary>Waits until the extension has put its button on the page, and returns how many there are.</summary>
    public static async Task<int> WaitForOurButtonAsync(this CdpClient tab, int timeoutMs = 20_000)
    {
        await tab.WaitForAsync($"document.querySelector({Js.Quote(OurButton)})", timeoutMs);
        return await tab.CountAsync(OurButton);
    }

    public static async Task<int> CountAsync(this CdpClient tab, string selector) =>
        await tab.EvaluateAsync<int>($"document.querySelectorAll({Js.Quote(selector)}).length");

    /// <summary>Clicks an element of the page with the mouse, as a user does: the browser sees real input, so what follows counts as the user's doing.</summary>
    public static async Task ClickAsync(this CdpClient tab, string selector)
    {
        // The first line of the element: a link that wraps has a box whose center may be over something else.
        var center = await tab.EvaluateAsync<double[]>(
            $$"""
            (() => {
              const element = document.querySelector({{Js.Quote(selector)}});
              if (!element) throw new Error('There is nothing at ' + {{Js.Quote(selector)}});
              element.scrollIntoView({ block: 'center', inline: 'center' });
              const box = element.getClientRects()[0] || element.getBoundingClientRect();
              return [box.left + box.width / 2, box.top + box.height / 2];
            })()
            """) ?? throw new InvalidOperationException("No position for " + selector);
        await tab.ClickAtAsync(center[0], center[1]);
    }

    /// <summary>Clicks at a point of the page (in CSS pixels, from its top left corner).</summary>
    public static async Task ClickAtAsync(this CdpClient tab, double x, double y)
    {
        await tab.SendAsync("Input.dispatchMouseEvent", new { type = "mouseMoved", x, y });
        await tab.SendAsync("Input.dispatchMouseEvent", new { type = "mousePressed", x, y, button = "left", buttons = 1, clickCount = 1 });
        await tab.SendAsync("Input.dispatchMouseEvent", new { type = "mouseReleased", x, y, button = "left", buttons = 0, clickCount = 1 });
    }

    /// <summary>The text of the toasts the extension shows on the page.</summary>
    public static async Task<string> ToastsAsync(this CdpClient tab) =>
        await tab.EvaluateAsync<string>($"(document.querySelector({Js.Quote(Toasts)}) || {{ textContent: '' }}).textContent") ?? "";

    /// <summary>What the extension answers to a message of the page, as the page gets it.</summary>
    public static Task<JsonElement> MessageAsync(this CdpClient tab, string json) =>
        tab.EvaluateAsync($"window.__qbtChrome.runtime.sendMessage({json})");
}
