using Arr411.Core.Files;

namespace Arr411.Core.Tests;

public class DownloadPolicyNameTests
{
    [Theory]
    [InlineData("film.torrent", "film.torrent")]
    [InlineData("Film (2024) 1080p.torrent", "Film (2024) 1080p.torrent")]
    [InlineData("été à la plage.torrent", "été à la plage.torrent")]
    [InlineData("..\\..\\evil.torrent", "evil.torrent")]
    [InlineData("../../evil.torrent", "evil.torrent")]
    [InlineData("C:\\x\\y.txt", "y.txt")]
    [InlineData("/etc/passwd", "passwd")]
    [InlineData("folder/", "fichier")]
    [InlineData("a:b.torrent", "a_b.torrent")]
    [InlineData("a<b>c|d?e*f\"g.torrent", "a_b_c_d_e_f_g.torrent")]
    [InlineData("a\u0000b.torrent", "a_b.torrent")]
    [InlineData("a\nb.torrent", "a_b.torrent")]
    [InlineData("film.torrent:Zone.Identifier", "film.torrent_Zone.Identifier")]
    [InlineData("file.torrent.", "file.torrent")]
    [InlineData("file.torrent. . ", "file.torrent")]
    [InlineData("  file.torrent  ", "file.torrent")]
    public void A_name_is_reduced_to_a_file_name_that_stays_in_the_folder(string name, string expected)
    {
        Assert.Equal(expected, DownloadPolicy.SafeName(name));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("...")]
    [InlineData(". .")]
    public void A_name_that_says_nothing_gets_a_default(string? name)
    {
        Assert.Equal("fichier", DownloadPolicy.SafeName(name));
    }

    [Fact]
    public void A_name_that_is_only_an_extension_keeps_it()
    {
        Assert.Equal("fichier.torrent", DownloadPolicy.SafeName(".torrent"));
    }

    [Theory]
    [InlineData("con.txt", "_con.txt")]
    [InlineData("CON", "_CON")]
    [InlineData("NUL", "_NUL")]
    [InlineData("nul.torrent", "_nul.torrent")]
    [InlineData("PRN.nfo", "_PRN.nfo")]
    [InlineData("AUX.torrent", "_AUX.torrent")]
    [InlineData("CON.tar.torrent", "_CON.tar.torrent")]
    [InlineData("con .txt", "_con.txt")]
    [InlineData("COM1.torrent", "_COM1.torrent")]
    [InlineData("com9.txt", "_com9.txt")]
    [InlineData("COM0.txt", "_COM0.txt")]
    [InlineData("LPT1.torrent", "_LPT1.torrent")]
    [InlineData("LPT9.txt", "_LPT9.txt")]
    [InlineData("LPT0.txt", "_LPT0.txt")]
    [InlineData("COM¹.txt", "_COM¹.txt")]
    [InlineData("COM².txt", "_COM².txt")]
    [InlineData("LPT³.txt", "_LPT³.txt")]
    [InlineData("CONIN$", "_CONIN$")]
    [InlineData("CONOUT$.txt", "_CONOUT$.txt")]
    public void A_device_name_is_never_used_as_it_is(string name, string expected)
    {
        Assert.Equal(expected, DownloadPolicy.SafeName(name));
    }

    [Theory]
    [InlineData("COM10.torrent")]
    [InlineData("console.torrent")]
    [InlineData("conclusion.txt")]
    [InlineData("nullable.torrent")]
    [InlineData("auxiliary.nfo")]
    [InlineData("com.torrent")]
    public void A_name_that_only_looks_like_a_device_is_left_alone(string name)
    {
        Assert.Equal(name, DownloadPolicy.SafeName(name));
    }

    [Fact]
    public void A_long_name_is_shortened_but_keeps_its_extension()
    {
        var name = DownloadPolicy.SafeName(new string('a', 300) + ".torrent");

        Assert.Equal(120, name.Length);
        Assert.EndsWith(".torrent", name);
        Assert.StartsWith("aaaa", name);
    }

    [Fact]
    public void A_name_is_never_cut_in_the_middle_of_a_surrogate_pair()
    {
        var cut = DownloadPolicy.SafeName(new string('a', 111) + "😀" + ".torrent");
        Assert.Equal(new string('a', 111) + ".torrent", cut);

        var kept = DownloadPolicy.SafeName(new string('a', 110) + "😀" + "bbbb.torrent");
        Assert.Equal(new string('a', 110) + "😀" + ".torrent", kept);
    }

    [Fact]
    public void A_very_long_extension_is_not_one()
    {
        var name = DownloadPolicy.SafeName("file." + new string('x', 20));

        Assert.Equal("file." + new string('x', 20), name);
        Assert.False(DownloadPolicy.IsAllowed(name));
    }

    [Fact]
    public void A_long_name_with_a_very_long_extension_is_still_cut()
    {
        var name = DownloadPolicy.SafeName(new string('a', 300) + "." + new string('b', 30));

        Assert.Equal(120, name.Length);
    }

    [Fact]
    public void The_extension_is_what_follows_the_last_dot()
    {
        Assert.Equal("film.v2.torrent", DownloadPolicy.SafeName("film.v2.torrent"));
        Assert.Equal("film.torrent.exe", DownloadPolicy.SafeName("film.torrent.exe"));
    }
}

public class DownloadPolicyAllowedTests
{
    [Theory]
    [InlineData("film.torrent")]
    [InlineData("FILM.TORRENT")]
    [InlineData("film.Torrent")]
    [InlineData("film.nfo")]
    [InlineData("film.txt")]
    [InlineData("film.srt")]
    [InlineData("film.sub")]
    [InlineData("film.zip")]
    [InlineData("film.rar")]
    [InlineData("film.7z")]
    [InlineData("poster.jpg")]
    [InlineData("poster.jpeg")]
    [InlineData("poster.png")]
    [InlineData("poster.gif")]
    [InlineData("poster.webp")]
    [InlineData("notes.pdf")]
    [InlineData("film.tar.zip")]
    public void The_harmless_kinds_are_allowed(string name)
    {
        Assert.True(DownloadPolicy.IsAllowed(name));
    }

    [Theory]
    [InlineData("setup.exe")]
    [InlineData("run.bat")]
    [InlineData("run.cmd")]
    [InlineData("run.ps1")]
    [InlineData("run.vbs")]
    [InlineData("run.js")]
    [InlineData("run.jar")]
    [InlineData("run.scr")]
    [InlineData("run.msi")]
    [InlineData("shortcut.lnk")]
    [InlineData("shortcut.url")]
    [InlineData("page.html")]
    [InlineData("page.htm")]
    [InlineData("image.svg")]
    [InlineData("film.torrent.exe")]
    [InlineData("film.torrent.")]
    [InlineData("film.torrent ")]
    [InlineData("film.torrent:stream")]
    [InlineData("film")]
    [InlineData("torrent")]
    [InlineData("")]
    public void Anything_else_is_refused(string name)
    {
        Assert.False(DownloadPolicy.IsAllowed(name));
    }

    [Fact]
    public void A_name_made_safe_by_SafeName_keeps_its_verdict()
    {
        Assert.True(DownloadPolicy.IsAllowed(DownloadPolicy.SafeName("..\\..\\film.torrent")));
        Assert.False(DownloadPolicy.IsAllowed(DownloadPolicy.SafeName("..\\..\\film.exe")));
        Assert.False(DownloadPolicy.IsAllowed(DownloadPolicy.SafeName("film.torrent:evil.exe")));
    }
}

public class DownloadPolicyFileTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private string Folder => _dir.File("Downloads");

    private string[] Files() => Directory.Exists(Folder)
        ? [.. new DirectoryInfo(Folder).GetFiles().Select(f => f.Name).Order(StringComparer.Ordinal)]
        : [];

    [Fact]
    public void A_file_is_reserved_empty_under_its_own_name_and_the_folder_is_created()
    {
        var path = DownloadPolicy.Reserve(Folder, "film.torrent");

        Assert.Equal(Path.Combine(Folder, "film.torrent"), path);
        Assert.True(File.Exists(path));
        Assert.Equal(0, new FileInfo(path).Length);
    }

    [Fact]
    public void A_reserved_file_is_not_held_open()
    {
        var path = DownloadPolicy.Reserve(Folder, "film.torrent");

        File.WriteAllBytes(path, [1, 2, 3]);

        Expect.Equal([1, 2, 3], File.ReadAllBytes(path));
    }

    [Fact]
    public void A_nested_folder_is_created_when_missing()
    {
        var folder = Path.Combine(Folder, "a", "b");

        var path = DownloadPolicy.Reserve(folder, "film.torrent");

        Assert.Equal(Path.Combine(folder, "film.torrent"), path);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void The_same_name_is_numbered_the_way_a_browser_does()
    {
        var first = DownloadPolicy.Reserve(Folder, "film.torrent");
        var second = DownloadPolicy.Reserve(Folder, "film.torrent");
        var third = DownloadPolicy.Reserve(Folder, "film.torrent");

        Assert.Equal("film.torrent", Path.GetFileName(first));
        Assert.Equal("film (1).torrent", Path.GetFileName(second));
        Assert.Equal("film (2).torrent", Path.GetFileName(third));
    }

    [Fact]
    public void A_name_without_extension_is_numbered_too()
    {
        DownloadPolicy.Reserve(Folder, "README");

        Assert.Equal("README (1)", Path.GetFileName(DownloadPolicy.Reserve(Folder, "README")));
    }

    [Fact]
    public void A_name_with_several_dots_is_numbered_before_the_last_extension()
    {
        DownloadPolicy.Reserve(Folder, "film.v2.torrent");

        Assert.Equal("film.v2 (1).torrent", Path.GetFileName(DownloadPolicy.Reserve(Folder, "film.v2.torrent")));
    }

    [Fact]
    public void An_existing_file_is_never_touched()
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllBytes(Path.Combine(Folder, "film.torrent"), [9, 9, 9]);

        var path = DownloadPolicy.Reserve(Folder, "film.torrent");

        Assert.Equal("film (1).torrent", Path.GetFileName(path));
        Expect.Equal([9, 9, 9], File.ReadAllBytes(Path.Combine(Folder, "film.torrent")));
    }

    [Fact]
    public void A_folder_of_the_same_name_is_walked_around()
    {
        Directory.CreateDirectory(Path.Combine(Folder, "film.torrent"));

        var path = DownloadPolicy.Reserve(Folder, "film.torrent");

        Assert.Equal("film (1).torrent", Path.GetFileName(path));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task Twenty_reservations_at_once_get_twenty_different_files()
    {
        var gate = new TaskCompletionSource();
        var tasks = Enumerable.Range(0, 20)
            .Select(_ => Task.Run(async () =>
            {
                await gate.Task;
                return DownloadPolicy.Reserve(Folder, "film.torrent");
            }))
            .ToArray();

        gate.SetResult();
        var paths = await Task.WhenAll(tasks);

        Assert.Equal(20, paths.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(paths, p => Assert.True(File.Exists(p)));
        Assert.Equal(20, Files().Length);
    }

    [Fact]
    public void Giving_up_on_a_name_taken_a_thousand_times_is_an_error_not_an_endless_loop()
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllBytes(Path.Combine(Folder, "a.torrent"), []);
        for (var i = 1; i < 1000; i++) File.WriteAllBytes(Path.Combine(Folder, $"a ({i}).torrent"), []);

        Assert.Throws<IOException>(() => DownloadPolicy.Reserve(Folder, "a.torrent"));
    }

    [Fact]
    public void Reserving_fails_when_the_folder_cannot_be_created()
    {
        File.WriteAllBytes(_dir.File("not-a-folder"), [1]);

        Assert.ThrowsAny<IOException>(() => DownloadPolicy.Reserve(Path.Combine(_dir.File("not-a-folder"), "sub"), "film.torrent"));
    }

    [Fact]
    public void An_empty_reserved_file_is_discarded()
    {
        var path = DownloadPolicy.Reserve(Folder, "film.torrent");

        DownloadPolicy.DiscardIfEmpty(path);

        Assert.False(File.Exists(path));
    }

    [Fact]
    public void A_file_with_content_is_kept()
    {
        var path = DownloadPolicy.Reserve(Folder, "film.torrent");
        File.WriteAllBytes(path, [1]);

        DownloadPolicy.DiscardIfEmpty(path);

        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Discarding_what_does_not_exist_does_nothing()
    {
        DownloadPolicy.DiscardIfEmpty(Path.Combine(Folder, "missing.torrent"));
        DownloadPolicy.DiscardIfEmpty(Path.Combine(_dir.File("missing-folder"), "missing.torrent"));
    }

    [Fact]
    public void A_folder_is_never_discarded()
    {
        var folder = Path.Combine(Folder, "film.torrent");
        Directory.CreateDirectory(folder);

        DownloadPolicy.DiscardIfEmpty(folder);

        Assert.True(Directory.Exists(folder));
    }

    [Fact]
    public void An_empty_file_that_is_still_open_is_left_alone()
    {
        Directory.CreateDirectory(Folder);
        var path = Path.Combine(Folder, "film.torrent");
        using var open = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);

        DownloadPolicy.DiscardIfEmpty(path);

        Assert.True(File.Exists(path));
    }
}
