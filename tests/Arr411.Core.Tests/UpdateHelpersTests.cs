using System.Net;
using System.Text.Json.Nodes;
using Arr411.Core.Bridge;
using Arr411.Core.Update;

namespace Arr411.Core.Tests;

public sealed class UpdateHelpersTests : IDisposable
{
    private const string Lower = "abcdef01";
    private const string Hash = Lower + Lower + Lower + Lower + Lower + Lower + Lower + Lower;
    private const string Upper = "ABCDEF01";
    private const string HashUpper = Upper + Upper + Upper + Upper + Upper + Upper + Upper + Upper;
    private const string Digits = "12345678";
    private const string OtherHash = Digits + Digits + Digits + Digits + Digits + Digits + Digits + Digits;
    private const string ShortHash = Lower + Lower + Lower + Lower + Lower + Lower + Lower + "abcdef0";

    private const string ProgramUrl = "https://github.com/HugoMeyer/arr411-desktop/releases/download/v0.3.0/Arr411.exe";
    private const string SumsUrl = "https://github.com/HugoMeyer/arr411-desktop/releases/download/v0.3.0/SHA256SUMS.txt";
    private const string PageUrl = "https://github.com/HugoMeyer/arr411-desktop/releases/tag/v0.3.0";

    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Theory]
    [InlineData("0.3.0", "0.2.1", true)]
    [InlineData("0.2.2", "0.2.1", true)]
    [InlineData("1.0.0", "0.9.9", true)]
    [InlineData("0.10.0", "0.9.0", true)]
    [InlineData("v0.3.0", "0.2.1", true)]
    [InlineData("0.2.1.1", "0.2.1", true)]
    [InlineData("0.2.1", "0.2.1", false)]
    [InlineData("0.2.1.0", "0.2.1", false)]
    [InlineData("0.2.0", "0.2.1", false)]
    [InlineData("0.9.0", "0.10.0", false)]
    [InlineData("0.3.0-beta", "0.2.1", false)]
    [InlineData("latest", "0.2.1", false)]
    [InlineData("", "0.2.1", false)]
    [InlineData(null, "0.2.1", false)]
    [InlineData("0.3.0", "not a version", false)]
    public void A_version_is_newer_only_when_the_numbers_say_so(string? candidate, string? current, bool expected) =>
        Assert.Equal(expected, UpdateVersion.IsNewer(candidate, current));

    [Theory]
    [InlineData("0.3.0", "0.3.0")]
    [InlineData("v0.3.0", "0.3.0")]
    [InlineData("V1.2", "1.2")]
    [InlineData(" 1.2.3.4 ", "1.2.3.4")]
    [InlineData("7", "7")]
    [InlineData("1.2.3.4.5", null)]
    [InlineData("1..2", null)]
    [InlineData("1.2.", null)]
    [InlineData("1.2.x", null)]
    [InlineData("1.2.3-rc1", null)]
    [InlineData("1234567890.1", null)]
    [InlineData("+1.2", null)]
    [InlineData("\u0661.\u0662", null)]
    [InlineData("v", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void A_version_is_one_to_four_numbers_with_an_optional_v(string? text, string? expected) =>
        Assert.Equal(expected, UpdateVersion.Clean(text));

    [Fact]
    public void The_numbers_of_a_version_are_padded_with_zeros()
    {
        Assert.Equal(new[] { 0, 3, 0, 0 }, UpdateVersion.Parse("v0.3")!);
        Assert.Equal(new[] { 1, 2, 3, 4 }, UpdateVersion.Parse("1.2.3.4")!);
        Assert.Null(UpdateVersion.Parse("1.2.3.4.5"));
    }

    [Theory]
    [InlineData("sha256:" + Hash, Hash)]
    [InlineData("SHA256:" + HashUpper, Hash)]
    [InlineData("sha256:" + HashUpper, Hash)]
    [InlineData("sha512:" + Hash, null)]
    [InlineData(Hash, null)]
    [InlineData("sha256:", null)]
    [InlineData("sha256:" + ShortHash, null)]
    [InlineData("sha256:" + Hash + "0", null)]
    [InlineData("sha256:g" + ShortHash, null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void The_digest_of_an_asset_is_its_sha256_in_lower_case_or_nothing(string? digest, string? expected) =>
        Assert.Equal(expected, UpdateHashes.ParseDigest(digest));

    [Theory]
    [InlineData(Hash + "  Arr411.exe\n")]
    [InlineData(Hash + "  Arr411.exe\r\n")]
    [InlineData(Hash + "  Arr411.exe")]
    [InlineData(Hash + " *Arr411.exe\n")]
    [InlineData(Hash + "\tArr411.exe\n")]
    [InlineData("\uFEFF" + Hash + "  Arr411.exe\r\n")]
    [InlineData(Hash + "  arr411.EXE\n")]
    [InlineData(HashUpper + "  Arr411.exe\n")]
    [InlineData(OtherHash + "  other.zip\r\n" + Hash + "  Arr411.exe\r\n")]
    [InlineData(Hash + "  Arr411.exe\n" + Hash + "  Arr411.exe\n")]
    public void The_hash_of_the_program_is_found_in_a_sums_file_however_it_is_written(string text) =>
        Assert.Equal(Hash, UpdateHashes.FindInSums(text, "Arr411.exe"));

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("<html>Not Found</html>")]
    [InlineData(OtherHash + "  other.zip\n")]
    [InlineData(Hash + "  Arr411.exe.sig\n")]
    [InlineData(Hash + "Arr411.exe\n")]
    [InlineData(Hash + "0  Arr411.exe\n")]
    [InlineData(ShortHash + "  Arr411.exe\n")]
    [InlineData("# " + Hash + "  Arr411.exe\n")]
    [InlineData(Hash + "  Arr411.exe\n" + OtherHash + "  Arr411.exe\n")]
    public void A_sums_file_without_a_clear_line_for_the_program_gives_nothing(string? text) =>
        Assert.Null(UpdateHashes.FindInSums(text, "Arr411.exe"));

    [Theory]
    [InlineData("https://api.github.com/repos/HugoMeyer/arr411-desktop/releases/latest")]
    [InlineData(ProgramUrl)]
    [InlineData("https://objects.githubusercontent.com/github-production-release-asset/1/x")]
    [InlineData("https://release-assets.githubusercontent.com/github-production-release-asset/1/x?sp=r&sig=abc")]
    [InlineData("https://GITHUB.com/x")]
    [InlineData("https://github.com:443/x")]
    public void An_https_address_of_a_github_host_is_allowed(string address) =>
        Assert.True(UpdateHosts.IsAllowed(new Uri(address)));

    [Theory]
    [InlineData("http://github.com/x")]
    [InlineData("https://github.com:8443/x")]
    [InlineData("https://user@github.com/x")]
    [InlineData("https://user:password@github.com/x")]
    [InlineData("https://github.com@evil.example/x")]
    [InlineData("https://github.com.evil.example/x")]
    [InlineData("https://evilgithub.com/x")]
    [InlineData("https://www.github.com/x")]
    [InlineData("https://gist.github.com/x")]
    [InlineData("https://raw.githubusercontent.com/HugoMeyer/arr411-desktop/main/Arr411.exe")]
    [InlineData("https://githubusercontent.com/x")]
    [InlineData("https://127.0.0.1/x")]
    [InlineData("ftp://github.com/x")]
    [InlineData("file:///C:/Windows/System32/cmd.exe")]
    [InlineData(@"\\github.com\share\x")]
    public void Any_other_address_is_refused(string address) =>
        Assert.False(UpdateHosts.IsAllowed(new Uri(address)));

    [Fact]
    public void No_address_and_no_absolute_address_are_refused()
    {
        Assert.False(UpdateHosts.IsAllowed(null));
        Assert.False(UpdateHosts.IsAllowed(new Uri("/releases", UriKind.Relative)));
    }

    [Theory]
    [InlineData(HttpStatusCode.MovedPermanently, true)]
    [InlineData(HttpStatusCode.Found, true)]
    [InlineData(HttpStatusCode.SeeOther, true)]
    [InlineData(HttpStatusCode.TemporaryRedirect, true)]
    [InlineData(HttpStatusCode.PermanentRedirect, true)]
    [InlineData(HttpStatusCode.OK, false)]
    [InlineData(HttpStatusCode.NotModified, false)]
    [InlineData(HttpStatusCode.NotFound, false)]
    public void The_redirects_are_the_five_statuses_that_carry_a_location(HttpStatusCode status, bool expected) =>
        Assert.Equal(expected, UpdateHosts.IsRedirect(status));

    [Fact]
    public void A_text_is_cut_without_splitting_a_pair_of_surrogates()
    {
        var text = "ab\U0001F600cd";

        Assert.Equal("ab", UpdateText.Truncate(text, 3));
        Assert.Equal("ab\U0001F600", UpdateText.Truncate(text, 4));
        Assert.Equal(text, UpdateText.Truncate(text, 6));
        Assert.Equal("", UpdateText.Truncate(null, 5));
        Assert.Equal("", UpdateText.Truncate("", 5));
    }

    [Theory]
    [InlineData("La mise à jour a échoué.", "La mise à jour a échoué.")]
    [InlineData("  trimmed  \r\n", "trimmed")]
    [InlineData("two\nlines\there", "two lines here")]
    [InlineData("a<b>c", "a b c")]
    [InlineData("<script>x</script>", "script x /script")]
    [InlineData("bell\u0007and\u0000null", "bellandnull")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void A_message_read_from_a_file_is_one_line_of_plain_text(string? raw, string expected) =>
        Assert.Equal(expected, UpdateText.CleanMessage(raw));

    [Fact]
    public void A_message_read_from_a_file_is_short()
    {
        Assert.Equal(300, UpdateText.CleanMessage(new string('x', 1000)).Length);
    }

    [Theory]
    [InlineData("auto", "auto")]
    [InlineData("notify", "notify")]
    [InlineData("off", "off")]
    [InlineData("OFF", "off")]
    [InlineData("  Notify ", "notify")]
    [InlineData("", "auto")]
    [InlineData(null, "auto")]
    [InlineData("never", "auto")]
    [InlineData("0", "auto")]
    public void The_mode_is_one_of_three_and_auto_when_the_setting_is_anything_else(string? setting, string expected) =>
        Assert.Equal(expected, UpdateMode.From(setting));

    [Theory]
    [InlineData("off", true)]
    [InlineData(" OFF ", true)]
    [InlineData("Off", true)]
    [InlineData("on", false)]
    [InlineData("offf", false)]
    [InlineData("0", false)]
    [InlineData("false", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void The_updates_are_switched_off_by_the_word_off_only(string? value, bool expected) =>
        Assert.Equal(expected, UpdateOptions.IsOff(value));

    [Fact]
    public void The_updates_are_switched_off_by_the_environment_of_the_run()
    {
        var before = Environment.GetEnvironmentVariable(UpdateOptions.DisableVariable);
        try
        {
            Environment.SetEnvironmentVariable(UpdateOptions.DisableVariable, "off");
            Assert.True(UpdateOptions.DisabledByEnvironment());

            Environment.SetEnvironmentVariable(UpdateOptions.DisableVariable, null);
            Assert.False(UpdateOptions.DisabledByEnvironment());
        }
        finally
        {
            Environment.SetEnvironmentVariable(UpdateOptions.DisableVariable, before);
        }
    }

    [Fact]
    public void The_state_is_written_with_the_names_and_the_order_of_the_contract()
    {
        var state = new UpdateState(true, "0.2.1", "0.3.0", true, "downloading", 0.42, "notes", "https://github.com/x", 1730000000000, true, null, "auto");

        var json = state.ToJson();

        Assert.Equal(
            ["supported", "current", "latest", "available", "status", "progress", "notes", "url", "checkedAt", "canInstall", "error", "mode"],
            json.Select(property => property.Key));
        Assert.Equal(
            """{"supported":true,"current":"0.2.1","latest":"0.3.0","available":true,"status":"downloading","progress":0.42,"notes":"notes","url":"https://github.com/x","checkedAt":1730000000000,"canInstall":true,"error":null,"mode":"auto"}""",
            json.ToJsonString());
    }

    [Fact]
    public void What_is_not_known_yet_is_null_in_the_state()
    {
        var state = new UpdateState(false, "0.2.1", null, false, "idle", null, "", "https://github.com/x", null, false, "Erreur", "off");

        var json = state.ToJson();

        Assert.Null(json["latest"]);
        Assert.Null(json["progress"]);
        Assert.Null(json["checkedAt"]);
        Assert.Equal("Erreur", (string?)json["error"]);
        Assert.False((bool)json["supported"]!);
    }

    [Fact]
    public void A_release_is_read_with_its_program_its_hash_and_its_sums_file()
    {
        var release = ReleaseParser.Parse(ReleaseJson());

        Assert.NotNull(release);
        Assert.Equal("0.3.0", release.Version);
        Assert.Equal("v0.3.0", release.Tag);
        Assert.Equal("Notes", release.Notes);
        Assert.Equal(PageUrl, release.PageUrl);
        Assert.Equal(ProgramUrl, release.AssetUrl);
        Assert.Equal(1000, release.Size);
        Assert.Equal(Hash, release.Digest);
        Assert.Equal(SumsUrl, release.SumsUrl);
    }

    [Theory]
    [InlineData("draft")]
    [InlineData("prerelease")]
    public void A_draft_and_a_prerelease_are_not_offered(string flag) =>
        Assert.Null(ReleaseParser.Parse(ReleaseJson(root => root[flag] = true)));

    [Theory]
    [InlineData("nightly")]
    [InlineData("v0.3.0-beta")]
    [InlineData("release-3")]
    [InlineData("")]
    public void A_tag_that_is_not_a_version_is_not_offered(string tag) =>
        Assert.Null(ReleaseParser.Parse(ReleaseJson(root => root["tag_name"] = tag)));

    [Fact]
    public void A_tag_with_a_capital_v_gives_the_same_version()
    {
        var release = ReleaseParser.Parse(ReleaseJson(root => root["tag_name"] = "V0.3.0"));

        Assert.Equal("0.3.0", release?.Version);
        Assert.Equal("V0.3.0", release?.Tag);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("5")]
    [InlineData("null")]
    [InlineData("\"v0.3.0\"")]
    public void An_answer_that_is_not_a_release_is_unreadable(string json)
    {
        var failure = Assert.Throws<UpdateFailure>(() => ReleaseParser.Parse(json));

        Assert.Equal(UpdateText.UnreadableAnswer, failure.Message);
    }

    [Theory]
    [InlineData("https://evil.example/Arr411.exe")]
    [InlineData("http://github.com/HugoMeyer/arr411-desktop/releases/download/v0.3.0/Arr411.exe")]
    [InlineData("https://github.com:8443/HugoMeyer/arr411-desktop/releases/download/v0.3.0/Arr411.exe")]
    [InlineData("https://raw.githubusercontent.com/HugoMeyer/arr411-desktop/main/Arr411.exe")]
    [InlineData("file:///C:/Windows/System32/cmd.exe")]
    [InlineData("not an address")]
    public void An_address_of_the_program_that_is_not_github_https_is_dropped(string address)
    {
        var release = ReleaseParser.Parse(ReleaseJson(root => ((JsonObject)root["assets"]![0]!)["browser_download_url"] = address));

        Assert.NotNull(release);
        Assert.Null(release.AssetUrl);
        Assert.Equal(SumsUrl, release.SumsUrl);
    }

    [Fact]
    public void An_address_of_the_sums_file_that_is_not_github_is_dropped()
    {
        var release = ReleaseParser.Parse(ReleaseJson(root => ((JsonObject)root["assets"]![1]!)["browser_download_url"] = "https://evil.example/SHA256SUMS.txt"));

        Assert.Equal(ProgramUrl, release?.AssetUrl);
        Assert.Null(release?.SumsUrl);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(209715200, 209715200)]
    [InlineData(209715201, 0)]
    [InlineData(0, 0)]
    [InlineData(-5, 0)]
    public void A_size_outside_what_may_be_installed_is_no_size(long announced, long expected)
    {
        var release = ReleaseParser.Parse(ReleaseJson(root => ((JsonObject)root["assets"]![0]!)["size"] = announced));

        Assert.Equal(expected, release?.Size);
    }

    [Fact]
    public void A_size_that_is_not_a_whole_number_is_no_size()
    {
        Assert.Equal(0, ReleaseParser.Parse(ReleaseJson(root => ((JsonObject)root["assets"]![0]!)["size"] = "1000"))?.Size);
        Assert.Equal(0, ReleaseParser.Parse(ReleaseJson(root => ((JsonObject)root["assets"]![0]!)["size"] = 1000.5))?.Size);
    }

    [Theory]
    [InlineData("sha256:xyz")]
    [InlineData("md5:" + Hash)]
    [InlineData("")]
    public void A_digest_that_is_not_a_sha256_is_no_digest(string digest)
    {
        var release = ReleaseParser.Parse(ReleaseJson(root => ((JsonObject)root["assets"]![0]!)["digest"] = digest));

        Assert.NotNull(release);
        Assert.Null(release.Digest);
    }

    [Fact]
    public void The_assets_are_found_by_their_names_whatever_the_case_and_the_others_are_ignored()
    {
        var release = ReleaseParser.Parse(ReleaseJson(root =>
        {
            var assets = (JsonArray)root["assets"]!;
            ((JsonObject)assets[0]!)["name"] = "arr411.EXE";
            ((JsonObject)assets[1]!)["name"] = "sha256sums.TXT";
            assets.Add(new JsonObject { ["name"] = "Arr411-setup.msi", ["browser_download_url"] = "https://github.com/x/Arr411-setup.msi", ["size"] = 5 });
        }));

        Assert.Equal(ProgramUrl, release?.AssetUrl);
        Assert.Equal(SumsUrl, release?.SumsUrl);
    }

    [Fact]
    public void A_release_without_assets_is_read_with_nothing_to_download()
    {
        var release = ReleaseParser.Parse(ReleaseJson(root => root.Remove("assets")));

        Assert.NotNull(release);
        Assert.Null(release.AssetUrl);
        Assert.Null(release.Digest);
        Assert.Null(release.SumsUrl);
        Assert.Equal(0, release.Size);
    }

    [Fact]
    public void The_page_of_a_release_is_built_when_the_answer_gives_none_that_can_be_followed()
    {
        Assert.Equal(PageUrl, ReleaseParser.Parse(ReleaseJson(root => root.Remove("html_url")))?.PageUrl);
        Assert.Equal(PageUrl, ReleaseParser.Parse(ReleaseJson(root => root["html_url"] = "https://evil.example/page"))?.PageUrl);
    }

    [Fact]
    public void The_notes_are_the_first_four_thousand_characters()
    {
        Assert.Equal(4000, ReleaseParser.Parse(ReleaseJson(root => root["body"] = new string('x', 5000)))?.Notes.Length);
        Assert.Equal("", ReleaseParser.Parse(ReleaseJson(root => root["body"] = null))?.Notes);
        Assert.Equal("", ReleaseParser.Parse(ReleaseJson(root => root.Remove("body")))?.Notes);
    }

    [Fact]
    public void What_the_updater_remembers_comes_back_as_it_was_written()
    {
        var record = new UpdateRecord
        {
            App = "0.2.1",
            Etag = "W/\"release-1\"",
            CheckedAt = 1730000000000,
            Failed = "0.2.5",
            Notified = "Available:0.3.0",
            Release = new CachedRelease("0.3.0", "v0.3.0", "Notes é", PageUrl, ProgramUrl, 1000, Hash, SumsUrl),
            Staged = new StagedUpdate("0.3.0", "Arr411-0.3.0.exe", Hash, 1000),
            Applying = new ApplyAttempt("0.3.0", @"C:\Apps\Arr411.exe"),
        };

        var read = UpdateRecord.Parse(record.ToJson());

        Assert.Equal(record.App, read.App);
        Assert.Equal(record.Etag, read.Etag);
        Assert.Equal(record.CheckedAt, read.CheckedAt);
        Assert.Equal(record.Failed, read.Failed);
        Assert.Equal(record.Notified, read.Notified);
        Assert.Equal(record.Release, read.Release);
        Assert.Equal(record.Staged, read.Staged);
        Assert.Equal(record.Applying, read.Applying);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("5")]
    [InlineData("null")]
    public void A_state_file_that_is_not_one_reads_as_nothing_known(string? json)
    {
        var record = UpdateRecord.Parse(json);

        Assert.Null(record.App);
        Assert.Null(record.Etag);
        Assert.Null(record.Release);
        Assert.Null(record.Staged);
        Assert.Null(record.Applying);
        Assert.Null(record.Failed);
    }

    [Fact]
    public void A_state_file_edited_by_hand_is_checked_like_an_answer_of_the_network()
    {
        var record = UpdateRecord.Parse($$"""
            {
              "app": "0.2.1; calc",
              "etag": "line\r\nbreak",
              "notified": "{{new string('n', 201)}}",
              "failed": "0.3.0-beta",
              "checkedAt": "yesterday",
              "release": { "version": "0.3.0", "tag": "v0.3.0", "url": "https://evil.example/", "assetUrl": "https://evil.example/Arr411.exe", "sumsUrl": "http://github.com/x", "size": "big", "digest": "sha256:zz" },
              "staged": { "version": "0.3.0", "file": "..\\..\\evil.exe", "sha256": "{{Hash}}", "size": 1000 },
              "applying": { "version": "0.3.0" }
            }
            """);

        Assert.Null(record.App);
        Assert.Null(record.Etag);
        Assert.Null(record.Notified);
        Assert.Null(record.Failed);
        Assert.Null(record.CheckedAt);
        Assert.Null(record.Release?.AssetUrl);
        Assert.Null(record.Release?.SumsUrl);
        Assert.Equal("", record.Release?.PageUrl);
        Assert.Equal(0, record.Release?.Size);
        Assert.Null(record.Release?.Digest);
        Assert.Null(record.Staged);
        Assert.Null(record.Applying);
    }

    [Theory]
    [InlineData("\"file\": \"Arr411-0.3.0.exe\", \"sha256\": \"" + Hash + "\", \"size\": 0")]
    [InlineData("\"file\": \"Arr411-0.3.0.exe\", \"sha256\": \"" + Hash + "\", \"size\": -1")]
    [InlineData("\"file\": \"Arr411-0.3.0.exe\", \"sha256\": \"" + Hash + "\", \"size\": 209715201")]
    [InlineData("\"file\": \"Arr411-0.3.0.exe\", \"sha256\": \"" + ShortHash + "\", \"size\": 1000")]
    [InlineData("\"file\": \"Arr411-0.4.0.exe\", \"sha256\": \"" + Hash + "\", \"size\": 1000")]
    [InlineData("\"sha256\": \"" + Hash + "\", \"size\": 1000")]
    public void A_downloaded_file_is_only_remembered_when_everything_about_it_is_plausible(string fields)
    {
        var record = UpdateRecord.Parse("{ \"staged\": { \"version\": \"0.3.0\", " + fields + " } }");

        Assert.Null(record.Staged);
    }

    [Fact]
    public void A_downloaded_file_is_remembered_with_a_lower_case_hash()
    {
        var record = UpdateRecord.Parse("{ \"staged\": { \"version\": \"v0.3.0\", \"file\": \"Arr411-0.3.0.exe\", \"sha256\": \"" + HashUpper + "\", \"size\": 1000 } }");

        Assert.Equal(new StagedUpdate("0.3.0", "Arr411-0.3.0.exe", Hash, 1000), record.Staged);
    }

    [Fact]
    public void The_error_of_the_applier_is_read_once_with_the_version_it_was_about()
    {
        var folder = _dir.File("updates");
        Directory.CreateDirectory(folder);

        UpdateFiles.WriteError(folder, "0.3.0", "Le fichier est verrouillé.");
        var first = UpdateFiles.TakeError(folder);
        var second = UpdateFiles.TakeError(folder);

        Assert.Equal(("0.3.0", "Le fichier est verrouillé."), first);
        Assert.Null(second);
        Assert.False(File.Exists(UpdateFiles.ErrorFile(folder)));
    }

    [Fact]
    public void An_error_file_that_starts_with_no_version_is_read_whole_as_a_message()
    {
        var folder = _dir.File("updates");
        Directory.CreateDirectory(folder);
        File.WriteAllText(UpdateFiles.ErrorFile(folder), "oops\n<b>message</b>\n");

        Assert.Equal((null, "oops  b message /b"), UpdateFiles.TakeError(folder));
    }

    [Fact]
    public void There_is_no_error_when_the_applier_left_none()
    {
        Assert.Null(UpdateFiles.TakeError(_dir.File("updates")));
    }

    [Fact]
    public void The_updater_state_of_a_folder_that_does_not_exist_is_empty()
    {
        Assert.Null(UpdateFiles.Load(_dir.File("nothing")).Release);
        Assert.Equal("Arr411-0.3.0.exe", UpdateFiles.StagedName("0.3.0"));
    }

    [Fact]
    public void A_file_in_use_is_not_deleted_and_says_so()
    {
        var file = _dir.File("busy.exe");
        File.WriteAllText(file, "x");

        using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.False(UpdateFiles.TryDelete(file));
        }

        Assert.True(UpdateFiles.TryDelete(file));
        Assert.True(UpdateFiles.TryDelete(file));
    }

    [Fact]
    public void A_folder_is_writable_when_a_file_can_be_made_in_it_and_nothing_is_left()
    {
        Assert.True(UpdateProbe.CanWriteTo(_dir.Root));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_dir.Root));
        Assert.False(UpdateProbe.CanWriteTo(_dir.File("missing")));
        Assert.False(UpdateProbe.CanWriteTo(""));
        Assert.False(UpdateProbe.CanWriteTo(null));
    }

    [Fact]
    public void The_arguments_of_the_applier_are_read_whatever_their_order()
    {
        var request = ApplyRequest.Parse(["--apply-update", "--background", "--from", "v0.2.1", "--wait-pid", "123", "--restart", "--target", @"C:\Apps\Arr411.exe"]);

        Assert.Null(request.Problem);
        Assert.Equal(@"C:\Apps\Arr411.exe", request.Target);
        Assert.Equal(123, request.WaitPid);
        Assert.Equal("0.2.1", request.From);
        Assert.True(request.Restart);
        Assert.True(request.Background);
    }

    [Fact]
    public void The_arguments_of_the_applier_can_be_written_with_an_equals_sign()
    {
        var request = ApplyRequest.Parse(["--apply-update", @"--target=C:\Apps\Arr411.exe", "--wait-pid=9", "--from=0.2.1"]);

        Assert.Null(request.Problem);
        Assert.Equal(@"C:\Apps\Arr411.exe", request.Target);
        Assert.Equal(9, request.WaitPid);
        Assert.False(request.Restart);
        Assert.False(request.Background);
    }

    [Theory]
    [InlineData("--wait-pid", "abc")]
    [InlineData("--wait-pid", "0")]
    [InlineData("--wait-pid", "-5")]
    [InlineData("--wait-pid", "1e3")]
    [InlineData("--wait-pid", "99999999999")]
    [InlineData("--from", "0.2.1-beta")]
    [InlineData("--from", "latest")]
    public void A_wrong_value_is_a_problem(string name, string value)
    {
        var args = new List<string> { "--apply-update", "--target", @"C:\Apps\Arr411.exe", "--wait-pid", "5", name, value };

        Assert.NotNull(ApplyRequest.Parse(args).Problem);
    }

    [Fact]
    public void A_missing_target_or_process_is_a_problem()
    {
        Assert.Equal("--target is missing", ApplyRequest.Parse(["--apply-update", "--wait-pid", "5"]).Problem);
        Assert.Equal("--wait-pid is missing", ApplyRequest.Parse(["--apply-update", "--target", @"C:\Apps\Arr411.exe"]).Problem);
        Assert.NotNull(ApplyRequest.Parse(["--apply-update", "--wait-pid", "5", "--target"]).Problem);
    }

    [Theory]
    [InlineData(@"C:\Apps\Arr411.exe", null)]
    [InlineData(@"C:\Apps\arr411.EXE", null)]
    [InlineData(@"D:\a b\Sub Folder\Arr411.exe", null)]
    [InlineData(@"C:\Apps\..\Apps\Arr411.exe", null)]
    [InlineData(@"\\nas\share\Arr411.exe", null)]
    [InlineData(@"\\.\C:\Apps\Arr411.exe", "device path")]
    [InlineData(@"\\?\C:\Apps\Arr411.exe", "device path")]
    [InlineData(@"Arr411.exe", "relative path")]
    [InlineData(@"..\Arr411.exe", "relative path")]
    [InlineData(@"\Arr411.exe", "relative path")]
    [InlineData(@"C:Arr411.exe", "relative path")]
    [InlineData(@"C:\Apps\Arr411.dll", "not an .exe")]
    [InlineData(@"C:\Apps\Arr411", "not an .exe")]
    [InlineData(@"C:\Updates\Arr411-0.3.0.exe", "the applier itself")]
    [InlineData(@"C:\Windows\System32\cmd.exe", "not the program that was running")]
    [InlineData(@"C:\Apps\Other.exe", "not the program that was running")]
    [InlineData(@"C:\Apps\Arr411-0.3.0.exe", "not the program that was running")]
    [InlineData("", "no target")]
    [InlineData("   ", "no target")]
    public void A_target_is_checked_before_the_applier_replaces_it(string target, string? reason)
    {
        var request = new ApplyRequest(target, 1, null, false, false, null);

        Assert.Equal(reason, request.CheckTarget(@"C:\Updates\Arr411-0.3.0.exe", null, out _));
    }

    [Theory]
    [InlineData(@"C:\Apps\Arr411.exe:evil.exe")]
    [InlineData(@"C:\Apps\Arr411.exe::$DATA")]
    public void A_target_with_a_stream_name_is_refused(string target)
    {
        var request = new ApplyRequest(target, 1, null, false, false, null);

        Assert.True(request.CheckTarget(@"C:\Updates\Arr411-0.3.0.exe", null, out _) is "stream or device name" or "invalid path" or "not an .exe");
    }

    [Fact]
    public void A_program_with_another_name_is_replaced_only_when_the_app_said_it_was_running_it()
    {
        var request = new ApplyRequest(@"C:\Apps\Portable.exe", 1, null, false, false, null);
        const string self = @"C:\Updates\Arr411-0.3.0.exe";

        Assert.Null(request.CheckTarget(self, @"C:\Apps\Portable.exe", out var target));
        Assert.Equal(@"C:\Apps\Portable.exe", target);
        Assert.Null(request.CheckTarget(self, @"c:\apps\portable.EXE", out _));
        Assert.NotNull(request.CheckTarget(self, @"C:\Apps\Another.exe", out _));
        Assert.NotNull(request.CheckTarget(self, @"Portable.exe", out _));
        Assert.NotNull(request.CheckTarget(self, "", out _));
    }

    [Theory]
    [InlineData(null, "arr411-reglages.json")]
    [InlineData("", "arr411-reglages.json")]
    [InlineData("   ", "arr411-reglages.json")]
    [InlineData(".json", "arr411-reglages.json")]
    [InlineData("...", "arr411-reglages.json")]
    [InlineData("a/b/", "arr411-reglages.json")]
    [InlineData("réglages", "réglages.json")]
    [InlineData("REGLAGES.JSON", "REGLAGES.json")]
    [InlineData("export.txt", "export.txt.json")]
    [InlineData("nom.", "nom.json")]
    [InlineData("name   ", "name.json")]
    [InlineData("a<b>c|d?e*f\"g", "a_b_c_d_e_f_g.json")]
    [InlineData("tab\there", "tab_here.json")]
    [InlineData("com1", "_com1.json")]
    [InlineData("LPT3.txt", "_LPT3.txt.json")]
    [InlineData("nul.json", "_nul.json")]
    [InlineData("com0", "com0.json")]
    [InlineData("console", "console.json")]
    public void The_name_of_an_export_is_a_plain_name_of_a_json_file(string? requested, string expected) =>
        Assert.Equal(expected, SettingsExport.FileName(requested));

    [Fact]
    public void A_surrogate_without_its_partner_cannot_end_up_in_the_name_of_an_export()
    {
        var lone = new string(['a', (char)0xD800, 'b']);

        Assert.Equal("a_b.json", SettingsExport.FileName(lone));
    }

    [Fact]
    public void The_name_of_an_export_is_short_and_never_ends_in_half_a_character()
    {
        var long_ = SettingsExport.FileName(new string('x', 300));
        var pair = SettingsExport.FileName(new string('x', 94) + "\U0001F600yyyy");

        Assert.Equal(SettingsExport.MaxNameLength, long_.Length);
        Assert.EndsWith(".json", long_);
        Assert.Equal(new string('x', 94) + ".json", pair);
    }

    [Fact]
    public void An_export_fits_up_to_a_mebibyte_of_characters_and_of_utf8()
    {
        Assert.True(SettingsExport.Fits(new string('a', SettingsExport.MaxBytes)));
        Assert.False(SettingsExport.Fits(new string('a', SettingsExport.MaxBytes + 1)));
        Assert.True(SettingsExport.Fits(new string('€', 349525) + "a"));
        Assert.False(SettingsExport.Fits(new string('€', 349525) + "ab"));
    }

    private static string ReleaseJson(Action<JsonObject>? change = null)
    {
        var root = new JsonObject
        {
            ["tag_name"] = "v0.3.0",
            ["draft"] = false,
            ["prerelease"] = false,
            ["html_url"] = PageUrl,
            ["body"] = "Notes",
            ["assets"] = new JsonArray
            {
                new JsonObject { ["name"] = "Arr411.exe", ["browser_download_url"] = ProgramUrl, ["size"] = 1000, ["digest"] = "sha256:" + Hash },
                new JsonObject { ["name"] = "SHA256SUMS.txt", ["browser_download_url"] = SumsUrl, ["size"] = 77 },
            },
        };
        change?.Invoke(root);
        return root.ToJsonString();
    }
}
