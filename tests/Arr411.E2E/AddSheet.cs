namespace Arr411.E2E;

/// <summary>
/// The "Ajouter un torrent" sheet of the web app: what a magnet link or a .torrent file handed to the app opens, on the qBittorrent tab.
/// Its text box holds the links, its list the files, and its "Ajouter" button sends them to qBittorrent.
/// </summary>
internal static class AddSheet
{
    private const string OnQbit = "App.state.tab === 'qbit' && document.visibilityState === 'visible'";

    /// <summary>A condition (for <see cref="CdpClient.WaitForAsync"/>) that holds while no sheet or other overlay is open.</summary>
    public const string Closed = "document.querySelectorAll('#overlays .overlay').length === 0";

    /// <summary>A condition that holds while the sheet is on screen with exactly these links in its text box.</summary>
    public static string ShowingLinks(params string[] links) =>
        $"{OnQbit} && [...document.querySelectorAll('.sheet-body textarea.input')].some(area => area.value === {Js.Quote(string.Join('\n', links))})";

    /// <summary>A condition that holds while the sheet is on screen with this file in its list.</summary>
    public static string ShowingFile(string name) =>
        $"{OnQbit} && [...document.querySelectorAll('.sheet-body .group .item .t.nowrap')].some(item => item.textContent === {Js.Quote(name)})";

    /// <summary>Presses "Ajouter".</summary>
    public static Task ConfirmAsync(this AppProcess app) =>
        app.Page.EvaluateAsync(
            "[...document.querySelectorAll('.sheet-actions button.pri')].find(button => button.textContent.includes('Ajouter')).click()",
            userGesture: true);

    /// <summary>Closes whatever sheet is open, so the next test starts from a page with nothing over it.</summary>
    public static async Task DismissAsync(this AppProcess app)
    {
        await app.Page.EvaluateAsync("App.closeOverlays()");
        await app.Page.WaitForAsync(Closed);
    }
}
