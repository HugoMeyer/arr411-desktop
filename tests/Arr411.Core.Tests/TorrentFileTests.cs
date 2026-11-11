using System.Text;
using Arr411.Core.Files;
using Arr411.Core.Shell;

namespace Arr411.Core.Tests;

public class TorrentFileTests
{
    private static readonly byte[] Minimal = Encoding.ASCII.GetBytes("d4:infod4:name1:xee");

    [Fact]
    public void A_torrent_file_is_read_with_its_name()
    {
        using var dir = new TempDir();
        var path = dir.File("film.torrent");
        File.WriteAllBytes(path, Minimal);

        var torrent = TorrentFile.Read(path);

        Assert.Equal("film.torrent", torrent.Name);
        Assert.Equal(Minimal, torrent.Data);
    }

    [Fact]
    public void The_web_app_gets_the_name_and_the_content_in_base64()
    {
        var torrent = TorrentFile.FromBytes("film.torrent", Minimal);

        var json = torrent.ToJson();

        Assert.Equal(2, json.Count);
        Assert.Equal("film.torrent", json["name"]!.GetValue<string>());
        Assert.Equal(Convert.ToBase64String(Minimal), json["base64"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("film", "film.torrent")]
    [InlineData("film.torrent", "film.torrent")]
    [InlineData("FILM.TORRENT", "FILM.TORRENT")]
    [InlineData("film.Torrent", "film.Torrent")]
    [InlineData("film.torrent.bak", "film.torrent.bak.torrent")]
    public void The_name_always_ends_in_dot_torrent(string name, string expected)
    {
        Assert.Equal(expected, TorrentFile.FromBytes(name, Minimal).Name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_file_without_a_name_gets_a_default_one(string name)
    {
        Assert.Equal("fichier.torrent", TorrentFile.FromBytes(name, Minimal).Name);
    }

    [Fact]
    public void The_name_of_a_file_read_from_the_disk_is_the_one_of_the_file_not_of_its_folder()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.File("sous dossier"));
        var path = dir.File(@"sous dossier\Un film (2024).torrent");
        File.WriteAllBytes(path, Minimal);

        Assert.Equal("Un film (2024).torrent", TorrentFile.Read(path).Name);
    }

    [Fact]
    public void A_file_without_the_extension_is_read_and_named_like_a_torrent()
    {
        using var dir = new TempDir();
        var path = dir.File("film");
        File.WriteAllBytes(path, Minimal);

        Assert.Equal("film.torrent", TorrentFile.Read(path).Name);
    }

    [Fact]
    public void A_file_that_is_not_a_torrent_is_refused_with_its_name()
    {
        using var dir = new TempDir();
        var path = dir.File("notes.txt");
        File.WriteAllText(path, "hello");

        var error = Assert.Throws<TorrentFileException>(() => TorrentFile.Read(path));

        Assert.Equal("« notes.txt » n’est pas un fichier .torrent", error.Message);
    }

    [Fact]
    public void An_empty_file_is_not_a_torrent()
    {
        using var dir = new TempDir();
        var path = dir.File("vide.torrent");
        File.WriteAllBytes(path, []);

        var error = Assert.Throws<TorrentFileException>(() => TorrentFile.Read(path));

        Assert.Equal("« vide.torrent » n’est pas un fichier .torrent", error.Message);
    }

    [Fact]
    public void A_missing_file_cannot_be_read()
    {
        using var dir = new TempDir();

        var error = Assert.Throws<TorrentFileException>(() => TorrentFile.Read(dir.File("absent.torrent")));
        Assert.Equal("fichier illisible (absent.torrent)", error.Message);

        error = Assert.Throws<TorrentFileException>(() => TorrentFile.Read(dir.File(@"absent\absent.torrent")));
        Assert.Equal("fichier illisible (absent.torrent)", error.Message);
    }

    [Fact]
    public void A_folder_cannot_be_read()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.File("dossier.torrent"));

        var error = Assert.Throws<TorrentFileException>(() => TorrentFile.Read(dir.File("dossier.torrent")));

        Assert.Equal("fichier illisible (dossier.torrent)", error.Message);
    }

    [Fact]
    public void A_path_that_cannot_exist_cannot_be_read()
    {
        Assert.Throws<TorrentFileException>(() => TorrentFile.Read(""));
        Assert.Throws<TorrentFileException>(() => TorrentFile.Read("mauvais\0nom.torrent"));
    }

    [Fact]
    public void A_file_over_the_limit_is_refused_before_it_is_read()
    {
        using var dir = new TempDir();
        var path = dir.File("enorme.torrent");
        using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write))
        {
            stream.SetLength(TorrentFile.MaxBytes + 1L);
        }

        var error = Assert.Throws<TorrentFileException>(() => TorrentFile.Read(path));

        Assert.Equal("fichier trop gros (enorme.torrent)", error.Message);
    }

    [Fact]
    public void The_limit_is_twenty_mebibytes_included()
    {
        var atLimit = new byte[TorrentFile.MaxBytes];
        atLimit[0] = (byte)'d';
        Assert.Equal(TorrentFile.MaxBytes, TorrentFile.FromBytes("limite", atLimit).Data.Length);

        var error = Assert.Throws<TorrentFileException>(() => TorrentFile.FromBytes("limite", new byte[TorrentFile.MaxBytes + 1]));
        Assert.Equal("fichier trop gros (limite)", error.Message);
        Assert.Equal(20 * 1024 * 1024, TorrentFile.MaxBytes);
    }

    [Fact]
    public void A_file_that_another_program_is_still_writing_can_be_read()
    {
        using var dir = new TempDir();
        var path = dir.File("en cours.torrent");
        using var writer = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        writer.Write(Minimal);
        writer.Flush();

        Assert.Equal(Minimal, TorrentFile.Read(path).Data);
    }

    [Fact]
    public void A_file_that_does_not_start_like_a_torrent_is_refused_whatever_its_name()
    {
        var error = Assert.Throws<TorrentFileException>(() => TorrentFile.FromBytes("film.torrent", "<html>"u8.ToArray()));

        Assert.Equal("« film.torrent » n’est pas un fichier .torrent", error.Message);
    }
}

public class SharePayloadTests
{
    [Fact]
    public void Text_is_handed_over_as_it_is()
    {
        var payload = SharePayload.Text("magnet:?xt=urn:btih:abc&dn=Un film\nhttps://c411.org/");

        Assert.Single(payload);
        Assert.Equal("magnet:?xt=urn:btih:abc&dn=Un film\nhttps://c411.org/", payload["text"]!.GetValue<string>());
    }

    [Fact]
    public void Files_are_handed_over_with_their_name_and_content()
    {
        var first = TorrentFile.FromBytes("un.torrent", "d1:ae"u8.ToArray());
        var second = TorrentFile.FromBytes("deux", "d1:be"u8.ToArray());

        var payload = SharePayload.Files([first, second]);

        Assert.Single(payload);
        var files = payload["files"]!.AsArray();
        Assert.Equal(2, files.Count);
        Assert.Equal("un.torrent", files[0]!["name"]!.GetValue<string>());
        Assert.Equal(Convert.ToBase64String("d1:ae"u8), files[0]!["base64"]!.GetValue<string>());
        Assert.Equal("deux.torrent", files[1]!["name"]!.GetValue<string>());
        Assert.Equal(Convert.ToBase64String("d1:be"u8), files[1]!["base64"]!.GetValue<string>());
    }

    [Fact]
    public void No_files_is_an_empty_list_not_a_missing_one()
    {
        var payload = SharePayload.Files([]);

        Assert.Empty(payload["files"]!.AsArray());
    }

    [Fact]
    public void The_settings_page_can_be_asked_for_with_or_without_a_section()
    {
        var plain = SharePayload.Settings();
        Assert.Single(plain);
        Assert.Equal("settings", plain["tab"]!.GetValue<string>());

        var section = SharePayload.Settings("qbit");
        Assert.Equal(2, section.Count);
        Assert.Equal("settings", section["tab"]!.GetValue<string>());
        Assert.Equal("qbit", section["section"]!.GetValue<string>());
    }
}
