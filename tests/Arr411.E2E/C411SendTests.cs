using System.Text;
using System.Text.Json.Nodes;

namespace Arr411.E2E;

/// <summary>Sending a torrent of C411 to qBittorrent from the button of the extension, and what happens to the other downloads of the tab.</summary>
[Trait("Category", "E2E")]
public sealed class C411SendTests(C411App fixture) : IClassFixture<C411App>
{
    private AppProcess App => fixture.App;

    private FakeServices Fake => fixture.Fake;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_torrent_goes_to_qbittorrent_as_the_dialog_says(bool paused)
    {
        var saved = Saved();

        var (tab, added) = await SendAsync($"/torrents/{FakeC411.Hash}", "films", paused);

        // The file of the site, untouched, with the choices of the dialog.
        Assert.Equal(FakeC411.Torrent, added.Content);
        Assert.Equal(FakeC411.TorrentName + ".torrent", added.FileName);
        Assert.Equal("films", added.Category);
        Assert.Equal(paused ? "true" : "", added.Paused);
        Assert.Equal(paused ? "true" : "", added.Stopped);

        // The extension says so, and keeps track of it.
        await tab.WaitForAsync($"document.querySelector({Js.Quote(C411Tab.Toasts)}) && document.querySelector({Js.Quote(C411Tab.Toasts)}).textContent.includes('Envoyé à qBittorrent (catégorie : films) ✔')");
        await App.WaitForStoredConfigAsync(
            config => config["tracked"] is JsonObject tracked && tracked.ContainsKey(FakeC411.Hash),
            "the torrent being tracked");

        // The file went to qBittorrent, not to the disk of the PC.
        Assert.Equal(saved, Saved());
        Assert.DoesNotContain(" ERROR ", App.ReadLog());
    }

    [Fact]
    public async Task A_torrent_the_site_builds_in_the_page_goes_to_qbittorrent_too()
    {
        var saved = Saved();

        var (_, added) = await SendAsync($"/torrents/{FakeC411.Hash}?mode=blob", "series", paused: false);

        Assert.Equal(FakeC411.Torrent, added.Content);
        Assert.Equal(FakeC411.TorrentName + ".torrent", added.FileName);
        Assert.Equal("series", added.Category);
        Assert.Equal(saved, Saved());
        Assert.DoesNotContain(" ERROR ", App.ReadLog());
    }

    [Fact]
    public async Task The_dialog_starts_with_the_category_of_the_last_send()
    {
        await SendAsync($"/torrents/{FakeC411.Hash}", "series", paused: false);

        var tab = await App.OpenAsync($"/torrents/{FakeC411.Hash}");
        await tab.WaitForOurButtonAsync();
        await tab.ClickAsync(C411Tab.OurButton);
        var dialog = await SendDialog.FindAsync(tab);
        await dialog.WaitUntilReadyAsync();

        Assert.Equal(new[] { "", "films", "series" }, await dialog.CategoriesAsync());
        Assert.Equal("series", await dialog.SelectedCategoryAsync());
        await dialog.CancelAsync();
        for (var attempt = 0; attempt < 50 && await dialog.IsShownAsync(); attempt++) await Task.Delay(100);
        Assert.False(await dialog.IsShownAsync());
    }

    [Fact]
    public async Task Cancelling_the_dialog_sends_nothing_and_saves_nothing()
    {
        var tab = await App.OpenAsync($"/torrents/{FakeC411.Hash}");
        var before = Fake.Added.Count;
        var saved = Saved();

        await tab.WaitForOurButtonAsync();
        await tab.ClickAsync(C411Tab.OurButton);
        var dialog = await SendDialog.FindAsync(tab);
        await dialog.WaitUntilReadyAsync();
        await dialog.CancelAsync();
        await Task.Delay(2_000);

        Assert.False(await dialog.IsShownAsync());
        Assert.Equal(before, Fake.Added.Count);
        Assert.Equal(saved, Saved());
        Assert.Equal(0, await tab.CountAsync("a[download]"));
    }

    [Fact]
    public async Task A_download_the_user_asks_for_is_saved_and_not_sent()
    {
        var tab = await App.OpenAsync($"/torrents/{FakeC411.Hash}");
        var before = Fake.Added.Count;

        // The download button of the site itself, not the one of the extension.
        await tab.ClickAsync("#download");

        var path = Path.Combine(App.DownloadsDir, FakeC411.TorrentName + ".torrent");
        var content = await ReadSavedAsync(path, FakeC411.Torrent.Length);
        Assert.Equal(FakeC411.Torrent, content);
        Assert.Equal(before, Fake.Added.Count);

        // Windows has to know that it comes from the Internet (SmartScreen, Protected View...).
        Assert.Contains("ZoneId=3", await ZoneOfAsync(path) ?? "(no mark)");
    }

    [Fact]
    public async Task A_program_is_not_saved_but_a_text_is()
    {
        var tab = await App.OpenAsync($"/torrents/{FakeC411.Hash}");

        await tab.ClickAsync("#exe");
        await tab.ClickAsync("#txt");

        // The text comes after the program: once it is there, the program has been dealt with.
        var content = await ReadSavedAsync(Path.Combine(App.DownloadsDir, "readme.txt"), 5);
        Assert.Equal("hello", Encoding.UTF8.GetString(content));
        Assert.False(File.Exists(Path.Combine(App.DownloadsDir, "setup.exe")));
    }

    private string[] Saved() => Directory.Exists(App.DownloadsDir) ? [.. Directory.GetFiles(App.DownloadsDir).Order()] : [];

    // Opens the page of a torrent, clicks the button of the extension, answers its dialog and waits for qBittorrent to get the file.
    private async Task<(CdpClient Tab, AddedTorrent Added)> SendAsync(string path, string category, bool paused)
    {
        var tab = await App.OpenAsync(path);
        var before = Fake.Added.Count;

        await tab.WaitForOurButtonAsync();
        await tab.ClickAsync(C411Tab.OurButton);
        var dialog = await SendDialog.FindAsync(tab);
        await dialog.WaitUntilReadyAsync();
        await dialog.ChooseCategoryAsync(category);
        await dialog.SetPausedAsync(paused);
        await dialog.SendAsync();

        await Wait.UntilAsync(() => Fake.Added.Count > before, "the torrent reaching qBittorrent", 30_000);
        return (tab, Fake.Added[before]);
    }

    // The mark of the Web (an alternate data stream of the file), or null when the file has none. The mark may come a moment after the data.
    private static async Task<string?> ZoneOfAsync(string path)
    {
        var deadline = Environment.TickCount64 + 5_000;
        while (true)
        {
            try
            {
                return await File.ReadAllTextAsync(path + ":Zone.Identifier");
            }
            catch (Exception ex) when (ex is IOException or NotSupportedException)
            {
                if (Environment.TickCount64 >= deadline) return null;
            }

            await Task.Delay(100);
        }
    }

    private static async Task<byte[]> ReadSavedAsync(string path, int length)
    {
        var deadline = Environment.TickCount64 + 20_000;
        while (Environment.TickCount64 < deadline)
        {
            try
            {
                if (File.Exists(path))
                {
                    var content = await File.ReadAllBytesAsync(path);
                    if (content.Length == length) return content;
                }
            }
            catch (IOException)
            {
                // The browser is still writing it.
            }

            await Task.Delay(100);
        }

        throw new TimeoutException("The file never got saved: " + path);
    }
}
