using System.Drawing;
using Arr411.Core.Shell;

namespace Arr411.Core.Tests;

public class ShortcutsTests
{
    private const int Tab = 0x09;
    private const int PageUp = 0x21;
    private const int PageDown = 0x22;
    private const int Left = 0x25;
    private const int R = 0x52;
    private const int F5 = 0x74;
    private const int BrowserBack = 0xA6;
    private const int Comma = 0xBC;

    private static Shortcut? Press(int key, bool ctrl = false, bool shift = false, bool alt = false) =>
        Shortcuts.Resolve(key, ctrl, shift, alt);

    private static void AssertIs(ShortcutKind kind, Shortcut? actual) => Assert.Equal<Shortcut?>(new Shortcut(kind), actual);

    [Theory]
    [InlineData(0x31, 0)]
    [InlineData(0x32, 1)]
    [InlineData(0x33, 2)]
    [InlineData(0x34, 3)]
    [InlineData(0x35, 4)]
    [InlineData(0x61, 0)]
    [InlineData(0x62, 1)]
    [InlineData(0x63, 2)]
    [InlineData(0x64, 3)]
    [InlineData(0x65, 4)]
    public void Ctrl_and_a_digit_show_that_tab_of_the_bar(int key, int index)
    {
        Assert.Equal<Shortcut?>(new Shortcut(ShortcutKind.SelectTab, Tabs.Bar[index]), Press(key, ctrl: true));
    }

    [Theory]
    [InlineData(0x30)]
    [InlineData(0x36)]
    [InlineData(0x39)]
    [InlineData(0x60)]
    [InlineData(0x66)]
    [InlineData(0x69)]
    public void There_are_only_five_tabs_to_select(int key)
    {
        Assert.Null(Press(key, ctrl: true));
    }

    [Fact]
    public void Digits_without_ctrl_or_with_other_modifiers_are_just_typed()
    {
        Assert.Null(Press(0x31));
        Assert.Null(Press(0x31, shift: true));
        Assert.Null(Press(0x31, alt: true));
        Assert.Null(Press(0x31, ctrl: true, shift: true));
        Assert.Null(Press(0x61, ctrl: true, shift: true));
    }

    [Fact]
    public void Ctrl_Tab_and_the_page_keys_go_through_the_tabs()
    {
        AssertIs(ShortcutKind.NextTab, Press(Tab, ctrl: true));
        AssertIs(ShortcutKind.PreviousTab, Press(Tab, ctrl: true, shift: true));
        AssertIs(ShortcutKind.NextTab, Press(PageDown, ctrl: true));
        AssertIs(ShortcutKind.PreviousTab, Press(PageUp, ctrl: true));

        Assert.Null(Press(Tab));
        Assert.Null(Press(Tab, shift: true));
        Assert.Null(Press(PageDown));
        Assert.Null(Press(PageUp, ctrl: true, shift: true));
    }

    [Fact]
    public void F5_and_Ctrl_R_reload()
    {
        AssertIs(ShortcutKind.Reload, Press(F5));
        AssertIs(ShortcutKind.Reload, Press(F5, ctrl: true));
        AssertIs(ShortcutKind.Reload, Press(F5, shift: true));
        AssertIs(ShortcutKind.Reload, Press(R, ctrl: true));

        Assert.Null(Press(R));
        Assert.Null(Press(R, ctrl: true, shift: true));
        Assert.Null(Press(F5, alt: true));
    }

    [Fact]
    public void Ctrl_comma_opens_the_settings()
    {
        AssertIs(ShortcutKind.Settings, Press(Comma, ctrl: true));

        Assert.Null(Press(Comma));
        Assert.Null(Press(Comma, ctrl: true, shift: true));
    }

    [Fact]
    public void The_back_key_and_Alt_Left_go_back()
    {
        AssertIs(ShortcutKind.Back, Press(BrowserBack));
        AssertIs(ShortcutKind.Back, Press(Left, alt: true));

        Assert.Null(Press(Left));
        Assert.Null(Press(Left, ctrl: true));
        Assert.Null(Press(Left, alt: true, shift: true));
        Assert.Null(Press(BrowserBack, ctrl: true));
        Assert.Null(Press(BrowserBack, shift: true));
    }

    [Theory]
    [InlineData(0x31)]
    [InlineData(0x61)]
    [InlineData(Comma)]
    [InlineData(R)]
    [InlineData(Tab)]
    [InlineData(F5)]
    [InlineData(PageUp)]
    [InlineData(PageDown)]
    [InlineData(Left)]
    [InlineData(BrowserBack)]
    public void AltGr_types_characters_and_is_never_a_shortcut(int key)
    {
        Assert.Null(Press(key, ctrl: true, alt: true));
        Assert.Null(Press(key, ctrl: true, shift: true, alt: true));
    }

    [Fact]
    public void Letters_alone_mean_nothing()
    {
        for (var key = 0x41; key <= 0x5A; key++) Assert.Null(Press(key));
    }
}

public class LaunchRequestTests
{
    private const string Work = @"C:\Work";

    private static LaunchRequest Parse(params string[] args) => LaunchRequest.Parse(args, Work);

    [Fact]
    public void Nothing_is_asked_without_arguments()
    {
        var request = Parse();

        Assert.Empty(request.Magnets);
        Assert.Empty(request.TorrentFiles);
        Assert.Null(request.Tab);
        Assert.Null(request.C411Url);
        Assert.False(request.Background);
        Assert.False(request.Quit);
        Assert.False(request.Register);
        Assert.False(request.Unregister);
        Assert.False(request.HasTorrents);
        Assert.False(request.HasWork);
        Assert.Equal(0, request.Ignored);
    }

    [Fact]
    public void The_empty_request_asks_for_nothing()
    {
        Assert.False(LaunchRequest.Empty.HasWork);
        Assert.Empty(LaunchRequest.Empty.Magnets);
        Assert.Empty(LaunchRequest.Empty.TorrentFiles);
    }

    [Fact]
    public void A_magnet_link_is_kept_as_it_was_given()
    {
        var request = Parse("magnet:?xt=urn:btih:ABC&dn=Un%20film");

        Assert.Equal(["magnet:?xt=urn:btih:ABC&dn=Un%20film"], request.Magnets);
        Assert.True(request.HasTorrents);
        Assert.True(request.HasWork);
        Assert.Equal(0, request.Ignored);
    }

    [Fact]
    public void The_scheme_of_a_magnet_link_may_be_in_capitals()
    {
        Assert.Single(Parse("MAGNET:?xt=urn:btih:abc").Magnets);
    }

    [Fact]
    public void Several_magnet_links_keep_their_order_up_to_the_limit()
    {
        var links = Enumerable.Range(0, LaunchRequest.MaxItems + 5).Select(i => $"magnet:?xt=urn:btih:{i:x4}").ToArray();

        var request = Parse(links);

        Assert.Equal(links.Take(LaunchRequest.MaxItems), request.Magnets);
        Assert.Equal(5, request.Ignored);
    }

    [Fact]
    public void Blank_arguments_are_skipped_without_a_fuss()
    {
        var request = Parse("", "   ", "  magnet:?xt=urn:btih:abc  ");

        Assert.Equal(["magnet:?xt=urn:btih:abc"], request.Magnets);
        Assert.Equal(0, request.Ignored);
    }

    [Theory]
    [InlineData("--background")]
    [InlineData("--BACKGROUND")]
    public void Background_starts_without_a_window(string option)
    {
        var request = Parse(option);

        Assert.True(request.Background);
        Assert.False(request.HasWork);
    }

    [Fact]
    public void The_other_options_are_recognised()
    {
        Assert.True(Parse("--quit").Quit);
        Assert.True(Parse("--register").Register);
        Assert.True(Parse("--unregister").Unregister);
        Assert.False(Parse("--quit").HasWork);
        Assert.False(Parse("--register").Unregister);
    }

    [Theory]
    [InlineData("--tab=qbit", "qbit")]
    [InlineData("--TAB=Radarr", "radarr")]
    [InlineData("--tab= sonarr ", "sonarr")]
    [InlineData("--tab=settings", "settings")]
    public void A_tab_can_be_asked_for_with_an_equals_sign(string argument, string expected)
    {
        var request = Parse(argument);

        Assert.Equal(expected, request.Tab);
        Assert.True(request.HasWork);
        Assert.Equal(0, request.Ignored);
    }

    [Fact]
    public void A_tab_can_be_asked_for_in_the_next_argument()
    {
        var request = Parse("--tab", "lidarr");

        Assert.Equal("lidarr", request.Tab);
        Assert.Equal(0, request.Ignored);
    }

    [Theory]
    [InlineData("--tab=bogus")]
    [InlineData("--tab=")]
    [InlineData("--tab")]
    public void An_unknown_tab_is_ignored(string argument)
    {
        var request = Parse(argument);

        Assert.Null(request.Tab);
        Assert.Equal(1, request.Ignored);
    }

    [Fact]
    public void Unknown_options_are_ignored()
    {
        var request = Parse("--frobnicate", "-x", "https://example.com/");

        Assert.False(request.HasWork);
        Assert.Equal(3, request.Ignored);
    }

    [Theory]
    [InlineData("--quit")]
    [InlineData("--tab=qbit")]
    [InlineData("--background")]
    [InlineData("--register")]
    [InlineData("--unregister")]
    public void Past_the_double_dash_nothing_is_an_option(string argument)
    {
        var request = Parse("--", argument);

        Assert.False(request.Quit);
        Assert.False(request.Background);
        Assert.False(request.Register);
        Assert.False(request.Unregister);
        Assert.Null(request.Tab);
        Assert.False(request.HasWork);
        Assert.Equal(1, request.Ignored);
    }

    [Fact]
    public void A_quote_in_a_link_cannot_add_options_to_the_command_line_of_the_registered_handler()
    {
        // The link  magnet:?xt=urn:btih:abc" --unregister "x  reaches the program as three arguments, all after the "--" put by the registered command.
        var request = Parse("--", "magnet:?xt=urn:btih:abc", "--unregister", "x");

        Assert.False(request.Unregister);
        Assert.Equal(["magnet:?xt=urn:btih:abc"], request.Magnets);
        Assert.Equal(2, request.Ignored);
    }

    [Fact]
    public void A_quote_in_a_link_cannot_make_the_program_open_files_of_its_choosing()
    {
        // The link  magnet:?xt=urn:btih:abc" "\\attacker\share\x.torrent" "C:\Users\me\y.torrent  is three arguments after the "--":
        // opening a file of a share would make Windows reach out to another machine with the credentials of the user.
        var request = Parse("--", "magnet:?xt=urn:btih:abc", @"\\attacker\share\x.torrent", @"C:\Users\me\y.torrent");

        Assert.Empty(request.TorrentFiles);
        Assert.Equal(["magnet:?xt=urn:btih:abc"], request.Magnets);
        Assert.Equal(2, request.Ignored);
    }

    [Fact]
    public void Only_the_first_argument_past_the_double_dash_counts()
    {
        var request = Parse("--", "magnet:?xt=urn:btih:abc", "magnet:?xt=urn:btih:def", "https://c411.org/torrents/1");

        Assert.Equal(["magnet:?xt=urn:btih:abc"], request.Magnets);
        Assert.Null(request.C411Url);
        Assert.Equal(2, request.Ignored);
    }

    [Fact]
    public void The_one_argument_past_the_double_dash_may_be_a_file_a_page_of_c411_or_a_link()
    {
        var file = Parse("--", @"C:\Films\Un film (2024).torrent");
        var page = Parse("--", "https://c411.org/torrents/1");

        Assert.Equal([@"C:\Films\Un film (2024).torrent"], file.TorrentFiles);
        Assert.Equal("https://c411.org/torrents/1", page.C411Url);
        Assert.Equal(0, file.Ignored + page.Ignored);
    }

    [Fact]
    public void A_blank_argument_past_the_double_dash_uses_up_the_place_of_the_link()
    {
        var request = Parse("--", "", "magnet:?xt=urn:btih:abc");

        Assert.False(request.HasWork);
        Assert.Equal(1, request.Ignored);
    }

    [Fact]
    public void An_argument_too_long_past_the_double_dash_uses_up_the_place_of_the_link()
    {
        var request = Parse("--", new string('a', LaunchRequest.MaxArgumentLength + 1), "magnet:?xt=urn:btih:abc");

        Assert.False(request.HasWork);
        Assert.Equal(2, request.Ignored);
    }

    [Fact]
    public void Several_items_are_given_without_the_double_dash()
    {
        var request = Parse("magnet:?xt=urn:btih:abc", @"C:\Films\a.torrent", @"C:\Films\b.torrent");

        Assert.Single(request.Magnets);
        Assert.Equal([@"C:\Films\a.torrent", @"C:\Films\b.torrent"], request.TorrentFiles);
        Assert.Equal(0, request.Ignored);
    }

    [Fact]
    public void Options_before_the_double_dash_still_count()
    {
        var request = Parse("--background", "--", "magnet:?xt=urn:btih:abc");

        Assert.True(request.Background);
        Assert.Single(request.Magnets);
    }

    [Fact]
    public void Dropped_files_are_all_taken_up_to_the_limit()
    {
        var files = Enumerable.Range(0, LaunchRequest.MaxItems + 2).Select(i => $@"C:\Films\f{i}.torrent").ToArray();

        var request = LaunchRequest.FromDroppedFiles(files);

        Assert.Equal(files.Take(LaunchRequest.MaxItems), request.TorrentFiles);
        Assert.Equal(2, request.Ignored);
    }

    [Fact]
    public void Only_the_torrent_files_among_the_dropped_ones_are_taken()
    {
        var request = LaunchRequest.FromDroppedFiles([@"C:\Films\a.torrent", @"C:\Films\notes.txt", @"C:\Windows\System32\calc.exe", @"D:\b.TORRENT"]);

        Assert.Equal([@"C:\Films\a.torrent", @"D:\b.TORRENT"], request.TorrentFiles);
        Assert.Empty(request.Magnets);
        Assert.Equal(2, request.Ignored);
    }

    [Theory]
    [InlineData("film.torrent")]
    [InlineData(@".\film.torrent")]
    [InlineData(@"\film.torrent")]
    [InlineData(@"\\.\pipe\film.torrent")]
    [InlineData(@"\\?\C:\film.torrent")]
    public void A_dropped_file_is_given_by_its_full_path(string path)
    {
        var request = LaunchRequest.FromDroppedFiles([path]);

        Assert.Empty(request.TorrentFiles);
        Assert.Equal(1, request.Ignored);
    }

    [Theory]
    [InlineData("--quit")]
    [InlineData("--tab=qbit")]
    [InlineData("--register")]
    public void Nothing_dropped_is_an_option(string name)
    {
        var request = LaunchRequest.FromDroppedFiles([name, @"C:\Films\a.torrent"]);

        Assert.False(request.Quit);
        Assert.False(request.Register);
        Assert.Null(request.Tab);
        Assert.Equal([@"C:\Films\a.torrent"], request.TorrentFiles);
        Assert.Equal(1, request.Ignored);
    }

    [Fact]
    public void Nothing_dropped_asks_for_nothing()
    {
        Assert.False(LaunchRequest.FromDroppedFiles([]).HasWork);
    }

    [Theory]
    [InlineData("https://c411.org/torrents/abc", "https://c411.org/torrents/abc")]
    [InlineData("http://www.c411.org/x?q=1", "https://www.c411.org/x?q=1")]
    [InlineData("  https://c411.org/  ", "https://c411.org/")]
    public void A_page_of_c411_is_shown_in_the_c411_tab(string argument, string expected)
    {
        var request = Parse(argument);

        Assert.Equal(expected, request.C411Url);
        Assert.True(request.HasWork);
        Assert.False(request.HasTorrents);
    }

    [Theory]
    [InlineData("https://example.com/")]
    [InlineData("https://c411.org.evil.example/")]
    [InlineData("https://c411.org:8443/")]
    [InlineData("https://user@c411.org/")]
    [InlineData("ftp://c411.org/")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ms-msdt:/id PCWDiagnostic")]
    public void Other_addresses_are_dropped(string argument)
    {
        var request = Parse(argument);

        Assert.Null(request.C411Url);
        Assert.False(request.HasWork);
        Assert.Equal(1, request.Ignored);
    }

    [Fact]
    public void Only_the_first_page_of_c411_is_kept()
    {
        var request = Parse("https://c411.org/a", "https://c411.org/b");

        Assert.Equal("https://c411.org/a", request.C411Url);
        Assert.Equal(1, request.Ignored);
    }

    [Theory]
    [InlineData("film.torrent", @"C:\Work\film.torrent")]
    [InlineData(@"sub\film.torrent", @"C:\Work\sub\film.torrent")]
    [InlineData(@".\film.torrent", @"C:\Work\film.torrent")]
    [InlineData(@"..\film.torrent", @"C:\film.torrent")]
    [InlineData("FILM.TORRENT", @"C:\Work\FILM.TORRENT")]
    [InlineData(@"D:\Downloads\Un film (2024).torrent", @"D:\Downloads\Un film (2024).torrent")]
    [InlineData(@"D:\Downloads\..\Other\x.torrent", @"D:\Other\x.torrent")]
    [InlineData(@"\\nas\share\film.torrent", @"\\nas\share\film.torrent")]
    public void A_torrent_file_is_found_from_the_folder_the_start_came_from(string argument, string expected)
    {
        var request = Parse(argument);

        Assert.Equal([expected], request.TorrentFiles);
        Assert.True(request.HasTorrents);
        Assert.Equal(0, request.Ignored);
    }

    [Theory]
    [InlineData("notes.txt")]
    [InlineData("film.torrent.exe")]
    [InlineData("torrent")]
    [InlineData(@"C:\Windows\System32\calc.exe")]
    [InlineData(@"\\.\pipe\film.torrent")]
    [InlineData(@"\\?\C:\film.torrent")]
    [InlineData(@"\\?\UNC\nas\share\film.torrent")]
    [InlineData(@"\film.torrent")]
    [InlineData("C:film.torrent")]
    public void Anything_but_a_plain_torrent_path_is_dropped(string argument)
    {
        var request = Parse(argument);

        Assert.Empty(request.TorrentFiles);
        Assert.False(request.HasWork);
        Assert.Equal(1, request.Ignored);
    }

    [Fact]
    public void A_path_with_a_null_character_is_dropped_without_an_error()
    {
        var request = Parse("a\0b.torrent");

        Assert.Empty(request.TorrentFiles);
        Assert.Equal(1, request.Ignored);
    }

    [Fact]
    public void White_space_around_a_path_is_not_part_of_it()
    {
        Assert.Equal([@"C:\Work\film.torrent"], Parse("  film.torrent \t").TorrentFiles);
    }

    [Fact]
    public void Relative_paths_need_a_known_folder_to_start_from()
    {
        Assert.Empty(LaunchRequest.Parse(["film.torrent"], "").TorrentFiles);
        Assert.Empty(LaunchRequest.Parse(["film.torrent"], "relative").TorrentFiles);
        Assert.Equal([@"D:\a\film.torrent"], LaunchRequest.Parse([@"D:\a\film.torrent"], "").TorrentFiles);
    }

    [Fact]
    public void At_most_twenty_files_are_kept()
    {
        var names = Enumerable.Range(0, LaunchRequest.MaxItems + 3).Select(i => $"f{i}.torrent").ToArray();

        var request = Parse(names);

        Assert.Equal(LaunchRequest.MaxItems, request.TorrentFiles.Count);
        Assert.Equal(3, request.Ignored);
    }

    [Fact]
    public void Everything_asked_at_once_is_understood()
    {
        var request = Parse("--tab=qbit", "magnet:?xt=urn:btih:abc", "film.torrent", "https://c411.org/torrents/1", "--background");

        Assert.Equal("qbit", request.Tab);
        Assert.Single(request.Magnets);
        Assert.Single(request.TorrentFiles);
        Assert.Equal("https://c411.org/torrents/1", request.C411Url);
        Assert.True(request.Background);
        Assert.Equal(0, request.Ignored);
    }

    [Fact]
    public void A_very_long_argument_is_dropped_but_the_limit_itself_is_allowed()
    {
        var prefix = "magnet:?xt=urn:btih:";
        var fits = prefix + new string('a', LaunchRequest.MaxArgumentLength - prefix.Length);
        var tooLong = fits + "a";

        var request = Parse(tooLong, fits);

        Assert.Equal([fits], request.Magnets);
        Assert.Equal(1, request.Ignored);
    }

    [Fact]
    public void Only_the_first_arguments_are_looked_at()
    {
        var junk = Enumerable.Repeat("junk", LaunchRequest.MaxArguments);

        Assert.Empty(Parse([.. junk, "magnet:?xt=urn:btih:abc"]).Magnets);
        Assert.Single(Parse([.. junk.Skip(1), "magnet:?xt=urn:btih:abc"]).Magnets);
    }

    [Fact]
    public void A_huge_list_of_arguments_is_cut_short()
    {
        var request = LaunchRequest.Parse(Enumerable.Repeat("magnet:?xt=urn:btih:abc", 100_000), Work);

        Assert.Equal(LaunchRequest.MaxItems, request.Magnets.Count);
        Assert.True(request.Ignored > 0);
    }

    [Fact]
    public void Files_are_taken_unless_the_caller_says_they_come_from_elsewhere()
    {
        Assert.Single(LaunchRequest.Parse([@"C:\Films\a.torrent"], Work).TorrentFiles);
        Assert.Single(LaunchRequest.Parse([@"C:\Films\a.torrent"], Work, allowFiles: true).TorrentFiles);

        var request = LaunchRequest.Parse([@"C:\Films\a.torrent", "film.torrent", @"\\machine\share\a.torrent", "magnet:?xt=urn:btih:abc"], Work, allowFiles: false);

        Assert.Empty(request.TorrentFiles);
        Assert.Single(request.Magnets);
        Assert.Equal(3, request.Ignored);
    }

    [Fact]
    public void Text_gives_its_magnet_links_and_its_page_of_c411()
    {
        var request = LaunchRequest.FromText("regarde ça :\r\nmagnet:?xt=urn:btih:abc\tmagnet:?xt=urn:btih:def  https://c411.org/torrents/1\n");

        Assert.Equal(["magnet:?xt=urn:btih:abc", "magnet:?xt=urn:btih:def"], request.Magnets);
        Assert.Equal("https://c411.org/torrents/1", request.C411Url);
        Assert.True(request.HasWork);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  \r\n\t ")]
    [InlineData("Blade Runner 2049")]
    [InlineData("https://example.org/film")]
    [InlineData("file:///C:/Windows/win.ini")]
    [InlineData("javascript:alert(1)")]
    public void Text_without_a_link_to_act_on_asks_for_nothing(string? text)
    {
        Assert.False(LaunchRequest.FromText(text).HasWork);
    }

    [Theory]
    [InlineData("--quit")]
    [InlineData("--tab=qbit")]
    [InlineData("--tab qbit")]
    [InlineData("--background")]
    [InlineData("--register")]
    [InlineData("--unregister")]
    public void Nothing_in_a_text_is_an_option(string text)
    {
        var request = LaunchRequest.FromText(text + " magnet:?xt=urn:btih:abc");

        Assert.False(request.Quit);
        Assert.False(request.Register);
        Assert.False(request.Unregister);
        Assert.False(request.Background);
        Assert.Null(request.Tab);
        Assert.Single(request.Magnets);
    }

    [Theory]
    [InlineData(@"C:\Users\me\Downloads\film.torrent")]
    [InlineData(@"\\127.0.0.1\share\film.torrent")]
    [InlineData(@"\\machine\c$\film.torrent")]
    [InlineData("film.torrent")]
    [InlineData("/etc/film.torrent")]
    public void A_path_in_a_text_is_never_taken_for_a_file_to_read(string path)
    {
        var request = LaunchRequest.FromText(path);

        Assert.Empty(request.TorrentFiles);
        Assert.False(request.HasWork);
    }

    [Fact]
    public void A_text_that_is_too_long_is_refused_whole_but_the_limit_itself_is_allowed()
    {
        const string Magnet = "magnet:?xt=urn:btih:abc";
        var fits = Magnet + " " + new string('a', LaunchRequest.MaxTextLength - Magnet.Length - 1);
        Assert.Equal(LaunchRequest.MaxTextLength, fits.Length);

        Assert.Single(LaunchRequest.FromText(fits).Magnets);

        var tooLong = LaunchRequest.FromText(fits + "a");
        Assert.False(tooLong.HasWork);
        Assert.Equal(1, tooLong.Ignored);
    }

    [Fact]
    public void A_text_with_countless_links_gives_the_first_ones_only()
    {
        var request = LaunchRequest.FromText(string.Join(' ', Enumerable.Repeat("magnet:?xt=urn:btih:abc", 2000)));

        Assert.Equal(LaunchRequest.MaxItems, request.Magnets.Count);
        Assert.True(request.Ignored > 0);
    }
}

public class WindowPlacementTests
{
    private static readonly Size Minimum = new(640, 480);
    private static readonly Size Usual = new(1100, 720);
    private static readonly Size Frame = new(16, 39);
    private static readonly Rectangle Main = new(0, 0, 1920, 1040);
    private static readonly Rectangle Right = new(1920, 0, 1920, 1080);
    private static readonly Rectangle Left = new(-1920, 0, 1920, 1040);

    private static WindowBounds? Fit(WindowBounds saved, params Rectangle[] screens) => WindowPlacement.Fit(saved, screens, Minimum);

    [Fact]
    public void A_window_that_is_on_a_screen_stays_where_it_was()
    {
        var saved = new WindowBounds(100, 80, 1100, 720, false);

        Assert.Equal<WindowBounds?>(saved, Fit(saved, Main));
    }

    [Fact]
    public void The_maximized_state_is_kept()
    {
        var saved = new WindowBounds(100, 80, 1100, 720, true);

        Assert.Equal<WindowBounds?>(saved, Fit(saved, Main));
    }

    [Fact]
    public void Without_a_screen_there_is_nothing_to_restore()
    {
        Assert.Null(Fit(new WindowBounds(0, 0, 800, 600, false)));
    }

    [Theory]
    [InlineData(0, 600)]
    [InlineData(800, 0)]
    [InlineData(-5, 600)]
    public void A_size_that_makes_no_sense_is_not_restored(int width, int height)
    {
        Assert.Null(Fit(new WindowBounds(10, 10, width, height, false), Main));
    }

    [Fact]
    public void A_window_left_on_an_unplugged_screen_is_not_restored()
    {
        Assert.Null(Fit(new WindowBounds(2500, 100, 1100, 720, false), Main));
        Assert.Null(Fit(new WindowBounds(1920, 0, 1100, 720, false), Main));
    }

    [Fact]
    public void A_window_on_the_second_screen_stays_when_that_screen_is_there()
    {
        var saved = new WindowBounds(2000, 50, 1100, 720, false);

        Assert.Equal<WindowBounds?>(saved, Fit(saved, Main, Right));
        Assert.Equal<WindowBounds?>(new WindowBounds(-1800, 100, 1100, 720, false), Fit(new WindowBounds(-1800, 100, 1100, 720, false), Left, Main));
    }

    [Fact]
    public void A_window_between_two_screens_stays_if_its_title_bar_can_be_grabbed()
    {
        var saved = new WindowBounds(1500, 100, 1100, 720, false);

        Assert.Equal<WindowBounds?>(saved, Fit(saved, Main, Right));
    }

    [Theory]
    [InlineData(1800, true)]
    [InlineData(1801, false)]
    [InlineData(-980, true)]
    [InlineData(-981, false)]
    public void Enough_of_the_title_bar_must_show_on_the_side(int x, bool kept)
    {
        var fit = Fit(new WindowBounds(x, 100, 1100, 720, false), Main);

        Assert.Equal(kept, fit is not null);
    }

    [Theory]
    [InlineData(1008, true)]
    [InlineData(1009, false)]
    [InlineData(-1, false)]
    [InlineData(-500, false)]
    public void The_title_bar_must_show_above_the_bottom_and_below_the_top(int y, bool kept)
    {
        var fit = Fit(new WindowBounds(100, y, 1100, 720, false), Main);

        Assert.Equal(kept, fit is not null);
    }

    [Fact]
    public void A_window_bigger_than_its_screen_is_shrunk_to_it()
    {
        var fit = Fit(new WindowBounds(0, 0, 5000, 3000, false), Main);

        Assert.Equal<WindowBounds?>(new WindowBounds(0, 0, 1920, 1040, false), fit);
    }

    [Fact]
    public void A_window_smaller_than_the_minimum_is_made_as_big_as_it()
    {
        var fit = Fit(new WindowBounds(100, 100, 200, 100, false), Main);

        Assert.Equal<WindowBounds?>(new WindowBounds(100, 100, 640, 480, false), fit);
    }

    [Fact]
    public void A_screen_smaller_than_the_minimum_wins()
    {
        var small = new Rectangle(0, 0, 600, 400);

        var fit = Fit(new WindowBounds(0, 0, 900, 650, false), small);

        Assert.Equal<WindowBounds?>(new WindowBounds(0, 0, 600, 400, false), fit);
    }

    [Fact]
    public void The_size_is_fitted_to_the_screen_the_window_is_mostly_on()
    {
        var narrow = new Rectangle(1920, 0, 1000, 700);

        var fit = Fit(new WindowBounds(1700, 100, 1500, 900, false), Main, narrow);

        Assert.Equal<WindowBounds?>(new WindowBounds(1700, 100, 1000, 700, false), fit);
    }

    [Fact]
    public void The_usual_size_is_kept_when_the_screen_has_room_for_it()
    {
        Assert.Equal(Usual, WindowPlacement.FitClient(Usual, Frame, Main));
    }

    [Fact]
    public void The_usual_size_is_shortened_to_leave_the_frame_on_the_screen()
    {
        // A 1080p laptop at 150 %: 720 dp are 1080 px, and the taskbar takes some of the screen.
        var fit = WindowPlacement.FitClient(new Size(1650, 1080), new Size(24, 59), new Rectangle(0, 0, 1920, 1020));

        Assert.Equal(new Size(1650, 961), fit);
    }

    [Fact]
    public void The_usual_size_is_narrowed_for_a_narrow_screen()
    {
        var fit = WindowPlacement.FitClient(Usual, Frame, new Rectangle(0, 0, 1024, 728));

        Assert.Equal(new Size(1008, 689), fit);
    }

    [Fact]
    public void The_usual_size_stays_positive_on_a_screen_that_is_smaller_than_the_frame()
    {
        Assert.Equal(new Size(1, 1), WindowPlacement.FitClient(Usual, Frame, new Rectangle(0, 0, 10, 10)));
    }
}
