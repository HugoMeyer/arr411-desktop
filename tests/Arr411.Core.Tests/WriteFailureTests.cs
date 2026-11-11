using System.Net;
using System.Security.AccessControl;
using System.Security.Principal;
using Arr411.Core.Config;
using Arr411.Core.Http;

namespace Arr411.Core.Tests;

/// <summary>A disk that refuses a write (read-only folder, antivirus holding a file...) must cost a log line, never the app.</summary>
public sealed class WriteFailureTests : IDisposable
{
    private const string Base = "http://radarr.test:7878";
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3];

    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private static ConfigStore OpenSettings(string path) => new(path, new ReversibleProtector());

    // A folder where the temporary copy of a file would go: the file cannot be written, whatever its rights.
    private static void BlockTemporaryCopy(string path) => Directory.CreateDirectory(path + ".tmp");

    [Fact]
    public void A_settings_file_that_cannot_be_written_keeps_the_change_in_memory_and_the_next_save_writes_everything()
    {
        var path = _dir.File("config.dat");
        var store = OpenSettings(path);
        store.Merge("""{"a":1}""", "test");
        BlockTemporaryCopy(path);
        var raised = 0;
        store.Changed += (_, _) => raised++;

        var changed = store.Merge("""{"b":2}""", "test");

        Assert.True(changed);
        Assert.Equal(1, raised);
        Assert.Equal("""{"a":1,"b":2}""", store.ToJson());
        Assert.Equal("""{"a":1}""", OpenSettings(path).ToJson());

        Directory.Delete(path + ".tmp");
        store.Merge("""{"c":3}""", "test");

        Assert.Equal("""{"a":1,"b":2,"c":3}""", OpenSettings(path).ToJson());
    }

    [Fact]
    public void Settings_that_cannot_be_saved_at_all_are_still_used_while_the_app_runs()
    {
        var blocker = _dir.File("blocker");
        File.WriteAllText(blocker, "a file where the folder should be");
        var store = OpenSettings(Path.Combine(blocker, "config.dat"));

        var changed = store.Merge("""{"radarrUrl":"http://nas:7878"}""", "test");

        Assert.True(changed);
        Assert.Equal("http://nas:7878", store.GetString("radarrUrl"));
        Assert.Equal("a file where the folder should be", File.ReadAllText(blocker));
    }

    [Fact]
    public void An_unreadable_settings_file_that_cannot_be_set_aside_stays_where_it_is_until_the_next_save_replaces_it()
    {
        var path = _dir.File("config.dat");
        File.WriteAllText(path, "not what the app wrote");
        Directory.CreateDirectory(path + ".bad");
        File.WriteAllText(Path.Combine(path + ".bad", "keep"), "x");

        var store = OpenSettings(path);

        Assert.Equal("{}", store.ToJson());
        Assert.Equal("not what the app wrote", File.ReadAllText(path));

        store.Merge("""{"a":1}""", "test");

        Assert.Equal("""{"a":1}""", OpenSettings(path).ToJson());
    }

    [Fact]
    public void A_window_state_that_cannot_be_written_is_kept_while_the_app_runs_and_written_by_the_next_change()
    {
        var path = _dir.File("ui.json");
        var state = new UiState(path);
        BlockTemporaryCopy(path);

        state.CloseToTray = false;
        state.LastTab = "sonarr";

        Assert.False(state.CloseToTray);
        Assert.Equal("sonarr", state.LastTab);
        Assert.False(File.Exists(path));

        Directory.Delete(path + ".tmp");
        state.TrayHintShown = true;

        var again = new UiState(path);
        Assert.False(again.CloseToTray);
        Assert.Equal("sonarr", again.LastTab);
        Assert.True(again.TrayHintShown);
    }

    [Fact]
    public async Task Covers_are_still_served_when_the_cache_cannot_be_written()
    {
        var blocker = _dir.File("covers");
        File.WriteAllText(blocker, "a file where the folder should be");
        using var handler = new StubHandler(_ => StubHandler.Reply(HttpStatusCode.OK, Jpeg, "image/jpeg"));
        using var proxy = new ImageProxy(new FakeConfig { ["radarrUrl"] = Base, ["radarrKey"] = "key" }, blocker, handler);

        var first = await proxy.GetAsync("radarr", Base + "/MediaCover/1/poster.jpg");
        var second = await proxy.GetAsync("radarr", Base + "/MediaCover/1/poster.jpg");

        Assert.Equal(200, first.Status);
        Assert.Equal(200, second.Status);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("a file where the folder should be", File.ReadAllText(blocker));
    }

    [Fact]
    public void A_cover_folder_that_cannot_be_listed_costs_a_log_line_and_nothing_else()
    {
        var folder = _dir.File("covers");
        var directory = Directory.CreateDirectory(folder);
        var rule = new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.ListDirectory, AccessControlType.Deny);
        var security = directory.GetAccessControl();
        security.AddAccessRule(rule);
        directory.SetAccessControl(security);
        try
        {
            using var proxy = new ImageProxy(new FakeConfig(), folder);

            Assert.Null(Record.Exception(proxy.Prune));
        }
        finally
        {
            security.RemoveAccessRule(rule);
            directory.SetAccessControl(security);
        }
    }

    [Fact]
    public void Pruning_goes_on_past_a_cover_another_program_holds_open()
    {
        var folder = _dir.File("covers");
        Directory.CreateDirectory(folder);
        var now = DateTime.UtcNow;
        string[] names = ["held", "free1", "free2"];
        for (var i = 0; i < names.Length; i++)
        {
            var file = Path.Combine(folder, names[i]);
            File.WriteAllBytes(file, new byte[100]);
            File.SetLastWriteTimeUtc(file, now - TimeSpan.FromHours(10 - i));
        }

        using var proxy = new ImageProxy(new FakeConfig(), folder);
        using var held = new FileStream(Path.Combine(folder, "held"), FileMode.Open, FileAccess.Read, FileShare.None);

        proxy.Prune(threshold: 100, target: 0);

        Assert.True(File.Exists(Path.Combine(folder, "held")));
        Assert.False(File.Exists(Path.Combine(folder, "free1")));
        Assert.False(File.Exists(Path.Combine(folder, "free2")));
    }
}
