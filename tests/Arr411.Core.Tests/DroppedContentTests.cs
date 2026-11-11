using System.Text;
using System.Text.Json;
using Arr411.Core.Files;
using Arr411.Core.Shell;

namespace Arr411.Core.Tests;

public class DroppedContentTests
{
    private const string Magnet = "magnet:?xt=urn:btih:abc";

    // A bencoded dictionary: all a .torrent has to look like here.
    private static readonly byte[] Torrent = "d4:spam4:eggse"u8.ToArray();

    private static string Base64(byte[]? data = null) => Convert.ToBase64String(data ?? Torrent);

    private static JsonElement Json(string json) => Json(Encoding.UTF8.GetBytes(json));

    private static JsonElement Json(byte[] utf8)
    {
        using var document = JsonDocument.Parse(utf8);
        return document.RootElement.Clone();
    }

    private static JsonElement Message(string? text, IEnumerable<(string? Name, string? Base64)> files)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("t", "drop");
            if (text is not null) writer.WriteString("text", text);

            writer.WriteStartArray("files");
            foreach (var (name, base64) in files)
            {
                writer.WriteStartObject();
                if (name is not null) writer.WriteString("name", name);
                if (base64 is not null) writer.WriteString("base64", base64);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Json(stream.ToArray());
    }

    private static JsonElement Message(string? text = null, params (string? Name, string? Base64)[] files) => Message(text, files.AsEnumerable());

    private static string[] Names(DroppedContent content) => [.. content.Files.Select(f => f.Name)];

    [Fact]
    public void Nothing_dropped_asks_for_nothing()
    {
        foreach (var content in new[] { DroppedContent.Empty, DroppedContent.Parse(Message()), DroppedContent.Parse(Json("{}")) })
        {
            Assert.False(content.HasWork);
            Assert.False(content.Request.HasWork);
            Assert.Empty(content.Files);
            Assert.Empty(content.Problems);
        }
    }

    [Theory]
    [InlineData("null")]
    [InlineData("42")]
    [InlineData("\"drop\"")]
    [InlineData("[]")]
    [InlineData("""[{"name":"film.torrent","base64":"ZA=="}]""")]
    public void A_message_that_is_not_an_object_asks_for_nothing(string json)
    {
        var content = DroppedContent.Parse(Json(json));

        Assert.False(content.HasWork);
        Assert.Empty(content.Problems);
    }

    [Theory]
    [InlineData("""{"files":"film.torrent"}""")]
    [InlineData("""{"files":{"name":"film.torrent","base64":"ZA=="}}""")]
    [InlineData("""{"files":null}""")]
    [InlineData("""{"files":7}""")]
    [InlineData("""{"files":[]}""")]
    [InlineData("""{"text":5}""")]
    [InlineData("""{"text":["magnet:?xt=urn:btih:abc"]}""")]
    public void Properties_of_the_wrong_kind_are_ignored(string json)
    {
        var content = DroppedContent.Parse(Json(json));

        Assert.False(content.HasWork);
        Assert.Empty(content.Problems);
    }

    [Fact]
    public void A_torrent_file_is_kept_with_its_content()
    {
        var content = DroppedContent.Parse(Message(null, ("film.torrent", Base64())));

        var file = Assert.Single(content.Files);
        Assert.Equal("film.torrent", file.Name);
        Expect.Equal(Torrent, file.Data);
        Assert.Empty(content.Problems);
        Assert.True(content.HasWork);
        Assert.False(content.Request.HasWork);
    }

    [Theory]
    [InlineData("FILM.TORRENT")]
    [InlineData("Un film (2049).Torrent")]
    [InlineData("été ✓.torrent")]
    public void The_extension_is_matched_whatever_its_case(string name)
    {
        var file = Assert.Single(DroppedContent.Parse(Message(null, (name, Base64()))).Files);

        Assert.Equal(name, file.Name);
    }

    [Theory]
    [InlineData("film.txt", "film.txt")]
    [InlineData("film.torrent.exe", "film.torrent.exe")]
    [InlineData("film", "film")]
    [InlineData(@"C:\Users\me\film.exe", "film.exe")]
    [InlineData("", "fichier")]
    public void A_file_that_is_not_called_torrent_is_refused(string name, string shown)
    {
        var content = DroppedContent.Parse(Message(null, (name, Base64())));

        Assert.Empty(content.Files);
        Assert.False(content.HasWork);
        Assert.Equal([$"« {shown} » n’est pas un fichier .torrent"], content.Problems);
    }

    [Fact]
    public void A_file_without_a_name_is_refused()
    {
        var content = DroppedContent.Parse(Message(null, (null, Base64())));

        Assert.Empty(content.Files);
        Assert.Equal(["« fichier » n’est pas un fichier .torrent"], content.Problems);
    }

    [Theory]
    [InlineData(@"..\..\Windows\evil.torrent", "evil.torrent")]
    [InlineData("C:/Users/me/film.torrent", "film.torrent")]
    [InlineData("a\u0001b.torrent", "a_b.torrent")]
    [InlineData("a:b*c?.torrent", "a_b_c_.torrent")]
    [InlineData("  film.torrent  ", "film.torrent")]
    [InlineData("CON.torrent", "_CON.torrent")]
    public void The_name_of_a_file_is_made_safe(string name, string expected)
    {
        var file = Assert.Single(DroppedContent.Parse(Message(null, (name, Base64()))).Files);

        Assert.Equal(expected, file.Name);
    }

    [Fact]
    public void A_very_long_name_is_cut_but_keeps_its_extension()
    {
        var file = Assert.Single(DroppedContent.Parse(Message(null, (new string('a', 500) + ".torrent", Base64()))).Files);

        Assert.Equal(120, file.Name.Length);
        Assert.EndsWith(".torrent", file.Name, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("%%%")]
    [InlineData("AQ")]
    [InlineData("not base64!")]
    public void Content_that_cannot_be_read_is_refused(string? base64)
    {
        var content = DroppedContent.Parse(Message(null, ("film.torrent", base64)));

        Assert.Empty(content.Files);
        Assert.Equal(["fichier illisible (film.torrent)"], content.Problems);
    }

    [Fact]
    public void Content_that_is_not_a_string_is_refused()
    {
        var content = DroppedContent.Parse(Json("""{"files":[{"name":"film.torrent","base64":5},{"name":"b.torrent","base64":["ZA=="]}]}"""));

        Assert.Empty(content.Files);
        Assert.Equal(["fichier illisible (film.torrent)", "fichier illisible (b.torrent)"], content.Problems);
    }

    [Theory]
    [InlineData("")]
    [InlineData("TVo=")]
    [InlineData("YWJj")]
    public void Content_that_is_not_a_torrent_is_refused(string base64)
    {
        var content = DroppedContent.Parse(Message(null, ("film.torrent", base64)));

        Assert.Empty(content.Files);
        Assert.Equal(["« film.torrent » n’est pas un fichier .torrent"], content.Problems);
    }

    [Fact]
    public void Line_breaks_in_the_base64_do_no_harm()
    {
        var text = Base64();
        var content = DroppedContent.Parse(Message(null, ("film.torrent", text[..8] + "\r\n" + text[8..])));

        Expect.Equal(Torrent, Assert.Single(content.Files).Data);
    }

    [Fact]
    public void A_file_of_the_biggest_size_is_kept_and_one_byte_more_is_refused()
    {
        var biggest = new byte[TorrentFile.MaxBytes];
        biggest[0] = (byte)'d';
        var tooBig = new byte[TorrentFile.MaxBytes + 1];
        tooBig[0] = (byte)'d';

        var kept = DroppedContent.Parse(Message(null, ("big.torrent", Base64(biggest))));
        var refused = DroppedContent.Parse(Message(null, ("big.torrent", Base64(tooBig))));

        Assert.Equal(TorrentFile.MaxBytes, Assert.Single(kept.Files).Data.Length);
        Assert.Empty(kept.Problems);
        Assert.Empty(refused.Files);
        Assert.Equal(["fichier trop gros (big.torrent)"], refused.Problems);
    }

    [Fact]
    public void Files_beyond_the_budget_of_one_drop_are_refused()
    {
        var content = DroppedContent.Parse(
            Message(null, ("a.torrent", Base64()), ("b.torrent", Base64()), ("c.torrent", Base64())),
            maxTotalBytes: 2 * Torrent.Length);

        Assert.Equal(["a.torrent", "b.torrent"], Names(content));
        Assert.Equal(["Trop de données d’un coup"], content.Problems);
    }

    [Fact]
    public void Only_the_first_files_are_looked_at()
    {
        var files = Enumerable.Range(0, DroppedContent.MaxFiles + 5).Select(i => ((string?)$"f{i}.torrent", (string?)Base64()));

        var content = DroppedContent.Parse(Message(null, files));

        Assert.Equal(DroppedContent.MaxFiles, content.Files.Count);
        Assert.Equal([$"Trop de fichiers : {DroppedContent.MaxFiles} au maximum"], content.Problems);
    }

    [Fact]
    public void Files_are_judged_one_by_one_and_keep_their_order()
    {
        var content = DroppedContent.Parse(Message(null, ("a.torrent", Base64()), ("b.png", Base64()), ("c.torrent", "%%%"), ("d.torrent", Base64())));

        Assert.Equal(["a.torrent", "d.torrent"], Names(content));
        Assert.Equal(["« b.png » n’est pas un fichier .torrent", "fichier illisible (c.torrent)"], content.Problems);
        Assert.True(content.HasWork);
    }

    [Fact]
    public void Entries_that_are_not_objects_are_refused_as_files_without_a_name()
    {
        var content = DroppedContent.Parse(Json("""{"files":[42,"film.torrent",null,[]]}"""));

        Assert.Empty(content.Files);
        Assert.Equal(4, content.Problems.Count);
        Assert.All(content.Problems, problem => Assert.Equal("« fichier » n’est pas un fichier .torrent", problem));
    }

    [Fact]
    public void Text_and_files_come_together()
    {
        var content = DroppedContent.Parse(Message(Magnet + " https://c411.org/torrents/1", ("film.torrent", Base64())));

        Assert.Equal([Magnet], content.Request.Magnets);
        Assert.Equal("https://c411.org/torrents/1", content.Request.C411Url);
        Assert.Single(content.Files);
        Assert.Empty(content.Problems);
    }

    [Fact]
    public void A_text_is_only_read_for_its_links()
    {
        var content = DroppedContent.Parse(Message("""--quit --tab=qbit \\127.0.0.1\share\film.torrent C:\Users\me\film.torrent file:///C:/film.torrent Blade Runner"""));

        Assert.False(content.HasWork);
        Assert.False(content.Request.Quit);
        Assert.Null(content.Request.Tab);
        Assert.Empty(content.Request.TorrentFiles);
        Assert.Empty(content.Problems);
    }

    [Fact]
    public void A_text_that_is_too_long_is_ignored()
    {
        var content = DroppedContent.Parse(Message(Magnet + " " + new string('a', LaunchRequest.MaxTextLength)));

        Assert.False(content.HasWork);
    }
}
