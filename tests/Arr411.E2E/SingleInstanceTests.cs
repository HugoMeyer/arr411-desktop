namespace Arr411.E2E;

/// <summary>
/// What a start of the program asks for. A second start hands its command line (a tab to show, a magnet link or a .torrent file to open) to the
/// app that runs, and ends; the commands that stop the app do not open a window.
/// </summary>
[Trait("Category", "E2E")]
public sealed class SingleInstanceTests(ShellApp shell) : IClassFixture<ShellApp>
{
    private static readonly string Magnet = $"magnet:?xt=urn:btih:{FakeC411.Hash}&dn=Blade.Runner.2049";
    private static readonly string OtherMagnet = "magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567&dn=Other";

    private AppProcess App => shell.App;

    [Theory]
    [InlineData("sonarr")]
    [InlineData("lidarr")]
    [InlineData("qbit")]
    [InlineData("c411")]
    public async Task A_second_start_shows_the_tab_it_is_asked_for_in_the_running_app(string tab)
    {
        await App.ShowTabAsync("radarr");

        Assert.Equal(0, await App.LaunchSecondAsync("--tab", tab));

        await App.ExpectTabAsync(tab);
        Assert.False(App.HasExited);
    }

    [Fact]
    public async Task The_tab_can_be_given_with_an_equals_sign()
    {
        await App.ShowTabAsync("radarr");

        Assert.Equal(0, await App.LaunchSecondAsync("--tab=lidarr"));

        await App.ExpectTabAsync("lidarr");
    }

    [Fact]
    public async Task A_second_start_with_nothing_to_do_brings_a_minimized_window_back()
    {
        await App.ShowTabAsync("radarr");
        var window = await App.WindowAsync();
        Win32.Minimize(window);
        await Wait.UntilAsync(() => Win32.IsMinimized(window), "the window being minimized");

        Assert.Equal(0, await App.LaunchSecondAsync());

        await Wait.UntilAsync(() => Win32.IsVisible(window) && !Win32.IsMinimized(window), "the window coming back");
        await App.ExpectTabAsync("radarr");
    }

    [Fact]
    public async Task A_link_to_c411_given_to_a_second_start_is_shown_in_the_C411_tab()
    {
        await App.ShowTabAsync("radarr");
        var opened = App.OpenedLinks().Count;

        Assert.Equal(0, await App.LaunchSecondAsync("--", $"https://c411.org/torrents/{FakeC411.Hash}"));

        await App.ExpectTabAsync("c411");
        var site = await App.C411Async();
        await site.WaitForAsync($"location.pathname === '/torrents/{FakeC411.Hash}' && document.readyState === 'complete'");
        Assert.Equal(opened, App.OpenedLinks().Count);
    }

    [Fact]
    public async Task A_magnet_link_given_to_a_second_start_is_offered_in_the_add_sheet_and_sent_to_qBittorrent_once_confirmed()
    {
        await App.ShowTabAsync("radarr");
        var before = shell.Fake.AddedLinks.Count;
        try
        {
            Assert.Equal(0, await App.LaunchSecondAsync("--", Magnet));

            await App.Page.WaitForAsync(AddSheet.ShowingLinks(Magnet));
            Assert.Equal(before, shell.Fake.AddedLinks.Count);

            await App.ConfirmAsync();

            await Wait.UntilAsync(() => shell.Fake.AddedLinks.Count > before, "the link reaching qBittorrent", 30_000);
            Assert.Equal(Magnet, Assert.Single(shell.Fake.AddedLinks.Skip(before)));
            await App.Page.WaitForAsync(AddSheet.Closed);
        }
        finally
        {
            await App.DismissAsync();
        }
    }

    [Fact]
    public async Task Several_magnet_links_are_offered_together()
    {
        await App.ShowTabAsync("radarr");
        try
        {
            Assert.Equal(0,             await App.LaunchSecondAsync(Magnet, OtherMagnet));

            await App.Page.WaitForAsync(AddSheet.ShowingLinks(Magnet, OtherMagnet));
        }
        finally
        {
            await App.DismissAsync();
        }
    }

    [Fact]
    public async Task A_torrent_file_given_to_a_second_start_is_offered_in_the_add_sheet_and_sent_to_qBittorrent_once_confirmed()
    {
        await App.ShowTabAsync("radarr");
        var path = Path.Combine(App.DataDir, "sample.torrent");
        await File.WriteAllBytesAsync(path, FakeC411.Torrent);
        var before = shell.Fake.Added.Count;
        try
        {
            Assert.Equal(0, await App.LaunchSecondAsync(path));

            await App.Page.WaitForAsync(AddSheet.ShowingFile("sample.torrent"));
            Assert.Equal(before, shell.Fake.Added.Count);

            await App.ConfirmAsync();

            await Wait.UntilAsync(() => shell.Fake.Added.Count > before, "the file reaching qBittorrent", 30_000);
            var added = Assert.Single(shell.Fake.Added.Skip(before));
            Assert.Equal("sample.torrent", added.FileName);
            Assert.Equal(FakeC411.Torrent, added.Content);
        }
        finally
        {
            await App.DismissAsync();
        }
    }

    [Fact]
    public async Task A_torrent_file_given_by_a_relative_path_is_looked_for_where_the_second_start_ran()
    {
        await App.ShowTabAsync("radarr");

        // Not the folder of the running app: the second start sends along where it ran.
        var elsewhere = Path.Combine(App.DataDir, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        await File.WriteAllBytesAsync(Path.Combine(elsewhere, "relative.torrent"), FakeC411.Torrent);
        try
        {
            Assert.Equal(0, await App.LaunchSecondFromAsync(elsewhere, "relative.torrent"));

            await App.Page.WaitForAsync(AddSheet.ShowingFile("relative.torrent"));
        }
        finally
        {
            await App.DismissAsync();
        }
    }

    [Fact]
    public async Task A_file_that_is_not_a_torrent_or_not_there_is_left_out_and_the_others_are_still_offered()
    {
        await App.ShowTabAsync("radarr");
        var notATorrent = Path.Combine(App.DataDir, "notes.torrent");
        var missing = Path.Combine(App.DataDir, "missing.torrent");
        var good = Path.Combine(App.DataDir, "good.torrent");
        await File.WriteAllTextAsync(notATorrent, "These are notes, not a torrent.");
        await File.WriteAllBytesAsync(good, FakeC411.Torrent);
        try
        {
            Assert.Equal(0, await App.LaunchSecondAsync(notATorrent, missing, good));

            await App.Page.WaitForAsync(AddSheet.ShowingFile("good.torrent"));
            Assert.False(await App.Page.EvaluateAsync<bool>(AddSheet.ShowingFile("notes.torrent")));
            Assert.False(await App.Page.EvaluateAsync<bool>(AddSheet.ShowingFile("missing.torrent")));
            Assert.False(App.HasExited);
        }
        finally
        {
            await App.DismissAsync();
        }
    }

    [Fact]
    public async Task Only_the_file_right_after_the_double_dash_is_opened()
    {
        await App.ShowTabAsync("radarr");
        var first = Path.Combine(App.DataDir, "first.torrent");
        var second = Path.Combine(App.DataDir, "second.torrent");
        await File.WriteAllBytesAsync(first, FakeC411.Torrent);
        await File.WriteAllBytesAsync(second, FakeC411.Torrent);
        try
        {
            // What a link holding a quote makes of the command line of the registered handler: what follows the link did not come from it.
            Assert.Equal(0, await App.LaunchSecondAsync("--", first, second));

            await App.Page.WaitForAsync(AddSheet.ShowingFile("first.torrent"));
            Assert.False(await App.Page.EvaluateAsync<bool>(AddSheet.ShowingFile("second.torrent")));
        }
        finally
        {
            await App.DismissAsync();
        }
    }

    [Fact]
    public async Task A_magnet_link_given_to_the_first_start_is_offered_once_the_app_is_ready()
    {
        await using var app = await AppProcess.StartAsync(ShellApp.Settings(shell.Fake), site: shell.Site, args: ["--", Magnet]);

        await app.Page.WaitForAsync(AddSheet.ShowingLinks(Magnet));
        await app.WindowAsync();
    }

    [Fact]
    public async Task Arguments_that_are_not_understood_are_ignored_and_never_acted_upon()
    {
        await using var app = await AppProcess.StartAsync();
        var opened = app.OpenedLinks().Count;

        // After "--" nothing is an option any more, even what looks like one. A web page and a program are not for the app to open.
        var code = await app.LaunchSecondAsync("--bogus", "--tab=nowhere", "--", "--quit", "https://example.org/", @"C:\Windows\System32\calc.exe");

        Assert.Equal(0, code);
        await app.WaitForLogAsync("Some arguments of a launch were ignored: 5");
        Assert.False(app.HasExited);
        Assert.Equal(opened, app.OpenedLinks().Count);
    }

    [Fact]
    public async Task The_quit_command_ends_the_running_app()
    {
        await using var app = await AppProcess.StartAsync();

        Assert.Equal(0, await app.LaunchSecondAsync("--quit"));

        Assert.Equal(0, await app.WaitForExitAsync());
        var log = app.ReadLog();
        Assert.DoesNotContain(" ERROR ", log);
        Assert.DoesNotContain(" WARN ", log);
    }

    [Fact]
    public async Task The_quit_command_does_nothing_when_the_app_is_not_running_in_that_data_folder()
    {
        var folder = AppProcess.NewDataDir();
        Directory.CreateDirectory(folder);
        try
        {
            var code = await AppProcess.RunAsync(folder, new Dictionary<string, string> { ["ARR411_DATA"] = folder }, ["--quit"]);

            Assert.Equal(0, code);

            // It started nothing, and the app of another data folder was left alone.
            Assert.False(Directory.Exists(Path.Combine(folder, "logs")));
            Assert.False(App.HasExited);
        }
        finally
        {
            AppProcess.TryDeleteDirectory(folder);
        }
    }
}
