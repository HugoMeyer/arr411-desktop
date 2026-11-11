using System.Collections.Specialized;
using System.Windows.Forms;

namespace Arr411.E2E;

/// <summary>
/// What the window does with what is dropped on it from outside, done with the real mouse (see <see cref="DragSource"/>). A magnet link or a
/// .torrent file is offered in the add sheet whichever tab is on screen, a link to C411 is shown in the C411 tab, and anything else is refused.
/// There are two ways in. The navigation bar is the window's own: it tells the cursor what it takes, so a drop it cannot use never happens.
/// A page belongs to the browser: a script of the app tells the window what was dropped on it, so the cursor accepts everything there and the
/// window says afterwards what it cannot use. What is dropped is never taken for an option of the program, nor loaded as a page, and a zone of
/// the site that takes a drop itself keeps it.
/// </summary>
[Trait("Category", "E2E")]
public sealed class DragDropTests(ShellApp shell) : IClassFixture<ShellApp>
{
    private const string NothingToAdd = "Rien à ajouter : dépose un lien magnet ou un fichier .torrent.";

    // The part of the bar under its last tab, where there is no button.
    private const int EmptyBar = 6;

    private static readonly string Magnet = $"magnet:?xt=urn:btih:{FakeC411.Hash}&dn=Blade.Runner.2049";
    private static readonly string OtherMagnet = "magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567&dn=Other";

    // What a test notes before a drop that must change nothing, and checks after it.
    private CdpClient _page = null!;
    private string _address = "";
    private int _opened;
    private int _added;

    /// <summary>Where a drop is made.</summary>
    public enum Target
    {
        /// <summary>The navigation bar, with the web app on screen.</summary>
        Bar,

        /// <summary>The web app.</summary>
        WebApp,

        /// <summary>The C411 site.</summary>
        Site,
    }

    public static TheoryData<Target> Targets { get; } = new() { Target.Bar, Target.WebApp, Target.Site };

    // The bar and the web app get every text; the site, which has the same script, only the ones that could do harm if it let them through.
    public static TheoryData<Target, string> NotLinks()
    {
        var data = new TheoryData<Target, string>();
        foreach (var text in new[]
        {
            "Blade Runner 2049",
            "--quit",
            "--tab=qbit",
            "--register",
            "https://example.org/",
            "file:///C:/Windows/win.ini",
            "javascript:alert(1)",
            @"\\127.0.0.1\share\film.torrent",
        })
        {
            data.Add(Target.Bar, text);
            data.Add(Target.WebApp, text);
        }

        data.Add(Target.Site, "--quit");
        data.Add(Target.Site, "https://example.org/");
        return data;
    }

    private AppProcess App => shell.App;

    [Theory]
    [MemberData(nameof(Targets))]
    public async Task A_magnet_link_is_offered_in_the_add_sheet_and_sent_to_qBittorrent_once_confirmed(Target target)
    {
        var before = shell.Fake.AddedLinks.Count;
        try
        {
            Assert.Equal(DragDropEffects.Copy, await DropAsync(target, () => Text(Magnet)));

            await App.Page.WaitForAsync(AddSheet.ShowingLinks(Magnet));
            Assert.Equal(before, shell.Fake.AddedLinks.Count);

            await App.ConfirmAsync();

            await Wait.UntilAsync(() => shell.Fake.AddedLinks.Count > before, "the link reaching qBittorrent", 30_000);
            Assert.Equal(Magnet, Assert.Single(shell.Fake.AddedLinks.Skip(before)));
        }
        finally
        {
            await App.DismissAsync();
        }
    }

    [Theory]
    [MemberData(nameof(Targets))]
    public async Task Several_links_dropped_as_one_text_are_offered_together(Target target)
    {
        try
        {
            Assert.Equal(DragDropEffects.Copy, await DropAsync(target, () => Text($"{Magnet}\r\n{OtherMagnet}")));

            await App.Page.WaitForAsync(AddSheet.ShowingLinks(Magnet, OtherMagnet));
        }
        finally
        {
            await App.DismissAsync();
        }
    }

    [Theory]
    [MemberData(nameof(Targets))]
    public async Task A_torrent_file_is_offered_in_the_add_sheet_and_the_page_under_it_does_not_load_it(Target target)
    {
        var path = Path.Combine(App.DataDir, "sample.torrent");
        await File.WriteAllBytesAsync(path, FakeC411.Torrent);
        await BeginAsync(target);
        try
        {
            Assert.Equal(DragDropEffects.Copy, await DropAsync(target, () => Files(path)));

            await App.Page.WaitForAsync(AddSheet.ShowingFile("sample.torrent"));
            Assert.Equal(_address, await _page.EvaluateAsync<string>("location.href"));
        }
        finally
        {
            await App.DismissAsync();
        }
    }

    [Theory]
    [MemberData(nameof(Targets))]
    public async Task Several_torrent_files_are_offered_together(Target target)
    {
        var first = Path.Combine(App.DataDir, "first.torrent");
        var second = Path.Combine(App.DataDir, "second.torrent");
        await File.WriteAllBytesAsync(first, FakeC411.Torrent);
        await File.WriteAllBytesAsync(second, FakeC411.Torrent);
        try
        {
            Assert.Equal(DragDropEffects.Copy, await DropAsync(target, () => Files(first, second)));

            await App.Page.WaitForAsync(AddSheet.ShowingFile("first.torrent"));
            await App.Page.WaitForAsync(AddSheet.ShowingFile("second.torrent"));
        }
        finally
        {
            await App.DismissAsync();
        }
    }

    [Theory]
    [MemberData(nameof(Targets))]
    public async Task A_link_to_C411_is_shown_in_the_C411_tab(Target target)
    {
        // The tab starts on another page of the site, so that the page of the link can only be there because of the drop.
        await App.OpenAsync("/");
        var opened = App.OpenedLinks().Count;

        Assert.Equal(DragDropEffects.Copy, await DropAsync(target, () => Text($"https://c411.org/torrents/{FakeC411.Hash}")));

        await App.ExpectTabAsync("c411");
        var site = await App.C411Async();
        await site.WaitForAsync($"location.pathname === '/torrents/{FakeC411.Hash}' && document.readyState === 'complete'");
        Assert.Equal(opened, App.OpenedLinks().Count);
    }

    [Theory]
    [MemberData(nameof(Targets))]
    public async Task A_file_that_is_not_a_torrent_is_refused(Target target)
    {
        var path = Path.Combine(App.DataDir, "notes.txt");
        await File.WriteAllTextAsync(path, "not a torrent");
        var message = target == Target.Bar ? null : "« notes.txt » n’est pas un fichier .torrent";
        await BeginAsync(target, message);

        Assert.Equal(EffectOfWhatIsRefused(target), await DropAsync(target, () => Files(path)));

        await ExpectNothingChangedAsync(target, message);
    }

    [Theory]
    [MemberData(nameof(NotLinks))]
    public async Task Text_that_is_neither_a_magnet_link_nor_a_link_to_C411_is_refused_and_taken_for_nothing_else(Target target, string text)
    {
        var message = target == Target.Bar ? null : NothingToAdd;
        await BeginAsync(target, message);

        Assert.Equal(EffectOfWhatIsRefused(target), await DropAsync(target, () => Text(text)));

        await ExpectNothingChangedAsync(target, message);
    }

    [Theory]
    [MemberData(nameof(Targets))]
    public async Task The_path_of_a_torrent_file_dropped_as_text_is_not_read(Target target)
    {
        var path = Path.Combine(App.DataDir, "typed.torrent");
        await File.WriteAllBytesAsync(path, FakeC411.Torrent);
        var message = target == Target.Bar ? null : NothingToAdd;
        await BeginAsync(target, message);

        Assert.Equal(EffectOfWhatIsRefused(target), await DropAsync(target, () => Text(path)));

        await ExpectNothingChangedAsync(target, message);
    }

    [Theory]
    [MemberData(nameof(Targets))]
    public async Task A_text_too_long_to_be_a_link_is_refused_even_when_it_starts_with_one(Target target)
    {
        var message = target == Target.Bar ? null : NothingToAdd;
        await BeginAsync(target, message);

        Assert.Equal(EffectOfWhatIsRefused(target), await DropAsync(target, () => Text(Magnet + " " + new string('x', 70_000))));

        await ExpectNothingChangedAsync(target, message);
    }

    [Fact]
    public async Task A_zone_of_the_site_that_takes_the_drop_keeps_it()
    {
        var path = Path.Combine(App.DataDir, "sample.torrent");
        await File.WriteAllBytesAsync(path, FakeC411.Torrent);
        var site = await App.OpenAsync("/dropzone");
        try
        {
            Assert.NotEqual(DragDropEffects.None, await DropAsync(Target.Site, () => Files(path)));

            await site.WaitForAsync("document.getElementById('files').textContent === 'sample.torrent'");

            // The app has not offered the file as well.
            await Task.Delay(500);
            await App.ExpectTabAsync("c411");
            Assert.True(await App.Page.EvaluateAsync<bool>(AddSheet.Closed));
        }
        finally
        {
            // The other tests drop on the site as it is when it is loaded: not on a zone that keeps everything.
            await App.OpenAsync("/");
        }
    }

    // The tab a drop on this target is made over.
    private static string TabOf(Target target) => target == Target.Site ? "c411" : "radarr";

    // What the cursor says of a drop the window cannot use: on the bar it says so; on a page, which the script cannot ask before the drop is
    // made, it does not.
    private static DragDropEffects EffectOfWhatIsRefused(Target target) => target == Target.Bar ? DragDropEffects.None : DragDropEffects.Copy;

    private async Task<DragDropEffects> DropAsync(Target target, Func<DataObject> data)
    {
        await App.ShowTabAsync(TabOf(target));
        var window = await App.WindowAsync();
        var (width, height) = Win32.ClientSize(window);
        var (x, y) = target == Target.Bar ? Win32.TabCenter(window, EmptyBar) : (width * 2 / 3, height / 2);
        return await DragSource.DropAsync(window, x, y, data);
    }

    // Puts the tab of the target on screen and notes what the app shows and has done, to see afterwards that a drop changed none of it.
    private async Task BeginAsync(Target target, string? message = null)
    {
        await App.ShowTabAsync(TabOf(target));
        _page = target == Target.Site ? await App.C411Async() : App.Page;
        _address = await _page.EvaluateAsync<string>("location.href") ?? "";
        _opened = App.OpenedLinks().Count;
        _added = shell.Fake.AddedLinks.Count;

        // The message of an earlier drop may still be on screen: it must not be taken for this one's.
        if (message is not null)
        {
            var window = await App.WindowAsync();
            await Wait.UntilAsync(() => !Win32.VisibleTexts(window).Contains(message), "the message of an earlier drop to go away", 10_000);
        }
    }

    // The window said what it had to say (on a page), and that is all it did: it is on the tab it was on, with no sheet open, the page where
    // it was, nothing handed to the browser of the user or to qBittorrent, and nothing wrong in its log.
    private async Task ExpectNothingChangedAsync(Target target, string? message)
    {
        if (message is not null)
        {
            var window = await App.WindowAsync();
            await Wait.UntilAsync(() => Win32.VisibleTexts(window).Contains(message), $"the message \"{message}\"", 10_000);
        }

        await Task.Delay(500);
        Assert.False(App.HasExited);
        await App.ExpectTabAsync(TabOf(target));
        Assert.True(await App.Page.EvaluateAsync<bool>(AddSheet.Closed));
        Assert.Equal(_address, await _page.EvaluateAsync<string>("location.href"));
        Assert.Equal(_opened, App.OpenedLinks().Count);
        Assert.Equal(_added, shell.Fake.AddedLinks.Count);

        var log = App.ReadLog();
        Assert.DoesNotContain(" ERROR ", log);
        Assert.DoesNotContain(" WARN ", log);
    }

    private static DataObject Text(string text) => new(DataFormats.UnicodeText, text);

    private static DataObject Files(params string[] paths)
    {
        var list = new StringCollection();
        list.AddRange(paths);
        var data = new DataObject();
        data.SetFileDropList(list);
        return data;
    }
}
