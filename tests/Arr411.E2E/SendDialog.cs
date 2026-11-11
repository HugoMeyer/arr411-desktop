using System.Text.Json;

namespace Arr411.E2E;

/// <summary>
/// The dialog the extension opens to ask where to send a torrent. It lives in a closed shadow root, which the page itself cannot reach:
/// the tests get to it through the DevTools protocol, which can.
/// </summary>
internal sealed class SendDialog
{
    private const string Looks = "function () { return !!this.querySelector('.cat') && !!this.querySelector('.ok'); }";

    private readonly CdpClient _tab;
    private readonly string _root;

    private SendDialog(CdpClient tab, string root)
    {
        _tab = tab;
        _root = root;
    }

    /// <summary>Waits for the dialog to be on the page. It is only good for that page: a navigation ends it.</summary>
    public static async Task<SendDialog> FindAsync(CdpClient tab, int timeoutMs = 15_000)
    {
        await tab.SendAsync("DOM.enable");
        var deadline = Environment.TickCount64 + timeoutMs;
        var last = "";
        while (Environment.TickCount64 < deadline)
        {
            try
            {
                if (await TryFindAsync(tab) is { } dialog) return dialog;
            }
            catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
            {
                // The page changed while it was being looked at: look again.
                last = ex.Message;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException("The dialog of the extension never showed up." + (last.Length > 0 ? " (" + last + ")" : ""));
    }

    /// <summary>Waits until the categories are known and the dialog can be answered (it is disabled while they load).</summary>
    public async Task WaitUntilReadyAsync(int timeoutMs = 15_000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            var ready = await CallAsync("function () { return !this.querySelector('.cat').disabled && !this.querySelector('.ok').disabled; }");
            if (ready.ValueKind == JsonValueKind.True) return;
            await Task.Delay(100);
        }

        throw new TimeoutException("The dialog of the extension never got ready.");
    }

    /// <summary>The categories the dialog offers (the first one, the empty one, is "no category").</summary>
    public async Task<string[]> CategoriesAsync() =>
        (await CallAsync("function () { return Array.from(this.querySelector('.cat').options).map(option => option.value); }"))
            .EnumerateArray().Select(value => value.GetString() ?? "").ToArray();

    /// <summary>The category that is selected.</summary>
    public async Task<string> SelectedCategoryAsync() =>
        (await CallAsync("function () { return this.querySelector('.cat').value; }")).GetString() ?? "";

    public async Task ChooseCategoryAsync(string category)
    {
        var chosen = await CallAsync(
            "function (category) { const list = this.querySelector('.cat'); list.value = category; list.dispatchEvent(new Event('change', { bubbles: true })); return list.value; }",
            category);
        if (chosen.GetString() != category) throw new InvalidOperationException($"The dialog does not offer the category '{category}'.");
    }

    public async Task SetPausedAsync(bool paused)
    {
        var state = await CallAsync("function (on) { const box = this.querySelector('input.paused'); if (box.checked !== on) box.click(); return box.checked; }", paused);
        if (state.ValueKind != (paused ? JsonValueKind.True : JsonValueKind.False)) throw new InvalidOperationException("The dialog would not take the pause option.");
    }

    public async Task<bool> IsShownAsync() =>
        (await CallAsync("function () { return this.host.isConnected; }")).ValueKind == JsonValueKind.True;

    /// <summary>Clicks "send" with the mouse, as a user does.</summary>
    public Task SendAsync() => ClickAsync(".ok");

    /// <summary>Clicks "cancel" with the mouse, as a user does.</summary>
    public Task CancelAsync() => ClickAsync(".cancel");

    private static async Task<SendDialog?> TryFindAsync(CdpClient tab)
    {
        var document = await tab.SendAsync("DOM.getDocument", new { depth = -1, pierce = true });
        var roots = new List<int>();
        CollectShadowRoots(document.GetProperty("root"), roots);

        foreach (var backendNodeId in roots)
        {
            string? objectId;
            try
            {
                var resolved = await tab.SendAsync("DOM.resolveNode", new { backendNodeId });
                objectId = resolved.GetProperty("object").GetProperty("objectId").GetString();
            }
            catch (InvalidOperationException)
            {
                // The node is gone already.
                continue;
            }

            if (objectId is null) continue;

            var probe = await tab.SendAsync("Runtime.callFunctionOn", new { objectId, functionDeclaration = Looks, returnByValue = true });
            if (probe.GetProperty("result").TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.True)
            {
                return new SendDialog(tab, objectId);
            }

            await tab.SendAsync("Runtime.releaseObject", new { objectId });
        }

        return null;
    }

    // The shadow roots of the whole tree, closed ones included (the protocol shows them), frames included.
    private static void CollectShadowRoots(JsonElement node, List<int> backendNodeIds)
    {
        if (node.TryGetProperty("shadowRoots", out var roots))
        {
            foreach (var root in roots.EnumerateArray())
            {
                backendNodeIds.Add(root.GetProperty("backendNodeId").GetInt32());
                CollectShadowRoots(root, backendNodeIds);
            }
        }

        if (node.TryGetProperty("children", out var children))
        {
            foreach (var child in children.EnumerateArray()) CollectShadowRoots(child, backendNodeIds);
        }

        if (node.TryGetProperty("contentDocument", out var frame)) CollectShadowRoots(frame, backendNodeIds);
    }

    private async Task ClickAsync(string selector)
    {
        var center = (await CallAsync(
            "function (selector) { const box = this.querySelector(selector).getBoundingClientRect(); return [box.left + box.width / 2, box.top + box.height / 2]; }",
            selector)).EnumerateArray().Select(value => value.GetDouble()).ToArray();
        await _tab.ClickAtAsync(center[0], center[1]);
    }

    private async Task<JsonElement> CallAsync(string function, params object[] arguments)
    {
        var result = await _tab.SendAsync("Runtime.callFunctionOn", new
        {
            objectId = _root,
            functionDeclaration = function,
            arguments = arguments.Select(value => new { value }).ToArray(),
            returnByValue = true,
            awaitPromise = true,
            userGesture = true,
        });
        if (result.TryGetProperty("exceptionDetails", out var details))
        {
            throw new InvalidOperationException("The dialog threw: " + details.GetRawText());
        }

        return result.GetProperty("result").TryGetProperty("value", out var value) ? value : default;
    }
}
