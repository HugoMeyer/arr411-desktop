using System.Text;
using System.Text.Json.Nodes;
using Arr411.Core.Config;
using Arr411.Core.Shell;

namespace Arr411.Core.Tests;

public class ConfigStoreTests
{
    private static ConfigStore Open(TempDir dir) => new(dir.File("config.dat"), new ReversibleProtector());

    [Fact]
    public void Starts_empty_when_there_is_no_file()
    {
        using var dir = new TempDir();

        Assert.Equal("{}", Open(dir).ToJson());
    }

    [Fact]
    public void Merged_settings_survive_a_restart_and_are_not_stored_in_clear()
    {
        using var dir = new TempDir();
        Assert.True(Open(dir).Merge("""{"radarrUrl":"http://nas:7878","radarrKey":"s3cret"}""", "test"));

        var reopened = Open(dir);

        Assert.Equal("http://nas:7878", reopened.GetString("radarrUrl"));
        Assert.Equal("s3cret", reopened.GetString("radarrKey"));
        var raw = Encoding.UTF8.GetString(File.ReadAllBytes(dir.File("config.dat")));
        Assert.DoesNotContain("s3cret", raw);
        Assert.DoesNotContain("radarrKey", raw);
    }

    [Fact]
    public void A_patch_only_touches_the_keys_it_contains()
    {
        using var dir = new TempDir();
        var store = Open(dir);
        store.Merge("""{"a":1,"b":2}""", "test");

        store.Merge("""{"b":3}""", "test");

        Assert.Equal("""{"a":1,"b":3}""", store.ToJson());
    }

    [Fact]
    public void Null_values_are_kept_as_keys()
    {
        using var dir = new TempDir();
        var store = Open(dir);

        Assert.True(store.Merge("""{"a":null}""", "test"));

        Assert.Equal("""{"a":null}""", store.ToJson());
        Assert.Null(store.GetString("a"));
    }

    [Fact]
    public void A_patch_that_changes_nothing_is_not_reported()
    {
        using var dir = new TempDir();
        var store = Open(dir);
        store.Merge("""{"a":{"x":[1,2]},"b":"t"}""", "test");
        var raised = 0;
        store.Changed += (_, _) => raised++;

        var changed = store.Merge("""{"a":{"x":[1,2]},"b":"t"}""", "test");

        Assert.False(changed);
        Assert.Equal(0, raised);
    }

    [Fact]
    public void The_change_event_carries_the_origin_and_only_the_keys_that_changed()
    {
        using var dir = new TempDir();
        var store = Open(dir);
        store.Merge("""{"a":1,"b":2}""", "test");
        ConfigChangedEventArgs? seen = null;
        store.Changed += (_, e) => seen = e;

        store.Merge("""{"a":1,"b":5,"c":"x"}""", "engine");

        Assert.NotNull(seen);
        Assert.Equal("engine", seen.Origin);
        Assert.Equal("""{"b":5,"c":"x"}""", seen.PatchJson);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("42")]
    [InlineData("null")]
    [InlineData("")]
    public void Patches_that_are_not_objects_are_ignored(string patch)
    {
        using var dir = new TempDir();
        var store = Open(dir);

        Assert.False(store.Merge(patch, "test"));
        Assert.Equal("{}", store.ToJson());
    }

    [Fact]
    public void The_store_keeps_its_own_copy_of_a_patch()
    {
        using var dir = new TempDir();
        var store = Open(dir);
        var patch = JsonNode.Parse("""{"o":{"x":1}}""")!.AsObject();
        store.Merge(patch, "test");

        patch["o"]!["x"] = 2;

        Assert.Equal("""{"o":{"x":1}}""", store.ToJson());
    }

    [Fact]
    public void GetString_ignores_missing_and_non_string_values()
    {
        using var dir = new TempDir();
        var store = Open(dir);
        store.Merge("""{"n":5,"s":"v","o":{}}""", "test");

        Assert.Equal("v", store.GetString("s"));
        Assert.Null(store.GetString("n"));
        Assert.Null(store.GetString("o"));
        Assert.Null(store.GetString("missing"));
    }

    [Fact]
    public void A_damaged_file_is_replaced_by_the_backup_copy()
    {
        using var dir = new TempDir();
        var store = Open(dir);
        store.Merge("""{"a":1}""", "test");
        store.Merge("""{"a":2}""", "test");
        File.WriteAllText(dir.File("config.dat"), "garbage");

        var reopened = Open(dir);

        Assert.Equal("""{"a":1}""", reopened.ToJson());
    }

    [Fact]
    public void An_unreadable_file_is_set_aside_and_the_store_starts_empty()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("config.dat"), "garbage");

        var store = Open(dir);

        Assert.Equal("{}", store.ToJson());
        Assert.True(File.Exists(dir.File("config.dat.bad")));
        Assert.False(File.Exists(dir.File("config.dat")));
    }

    [Fact]
    public void Settings_can_still_be_saved_after_a_failed_load()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("config.dat"), "garbage");
        var store = Open(dir);

        store.Merge("""{"a":1}""", "test");

        Assert.Equal("""{"a":1}""", Open(dir).ToJson());
    }

    [Fact]
    public void Concurrent_merges_do_not_lose_keys()
    {
        using var dir = new TempDir();
        var store = Open(dir);

        Parallel.For(0, 40, i => store.Merge($$"""{"k{{i}}":{{i}}}""", "test"));

        var reopened = Open(dir);
        for (var i = 0; i < 40; i++) Assert.Contains($"\"k{i}\":{i}", reopened.ToJson());
    }
}

public class UiStateTests
{
    [Fact]
    public void Nothing_is_remembered_at_first()
    {
        using var dir = new TempDir();

        Assert.Null(new UiState(dir.File("ui.json")).LastTab);
    }

    [Fact]
    public void The_last_tab_survives_a_restart()
    {
        using var dir = new TempDir();
        new UiState(dir.File("ui.json")).LastTab = "radarr";

        Assert.Equal("radarr", new UiState(dir.File("ui.json")).LastTab);
    }

    [Fact]
    public void A_damaged_file_is_ignored()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("ui.json"), "{ nope");

        var state = new UiState(dir.File("ui.json"));

        Assert.Null(state.LastTab);
        state.LastTab = "qbit";
        Assert.Equal("qbit", new UiState(dir.File("ui.json")).LastTab);
    }

    [Fact]
    public void Closing_goes_to_the_notification_area_until_told_otherwise()
    {
        using var dir = new TempDir();
        var state = new UiState(dir.File("ui.json"));
        Assert.True(state.CloseToTray);
        Assert.False(state.TrayHintShown);

        state.CloseToTray = false;
        state.TrayHintShown = true;

        var reopened = new UiState(dir.File("ui.json"));
        Assert.False(reopened.CloseToTray);
        Assert.True(reopened.TrayHintShown);

        reopened.CloseToTray = true;
        Assert.True(new UiState(dir.File("ui.json")).CloseToTray);
    }

    [Fact]
    public void The_window_position_survives_a_restart()
    {
        using var dir = new TempDir();
        Assert.Null(new UiState(dir.File("ui.json")).Window);

        new UiState(dir.File("ui.json")).Window = new WindowBounds(-1200, 40, 1100, 720, true);

        Assert.Equal(new WindowBounds(-1200, 40, 1100, 720, true), new UiState(dir.File("ui.json")).Window);
    }

    [Fact]
    public void A_saved_position_can_be_forgotten()
    {
        using var dir = new TempDir();
        var state = new UiState(dir.File("ui.json"));
        state.Window = new WindowBounds(0, 0, 800, 600, false);

        state.Window = null;

        Assert.Null(new UiState(dir.File("ui.json")).Window);
    }

    [Theory]
    [InlineData("""{"window":{"x":0,"y":0,"width":0,"height":600}}""")]
    [InlineData("""{"window":{"x":0,"y":0,"width":800,"height":-5}}""")]
    [InlineData("""{"window":{"x":2000000000,"y":0,"width":800,"height":600}}""")]
    [InlineData("""{"window":{"x":"0","y":0,"width":800,"height":600}}""")]
    [InlineData("""{"window":{"x":0.5,"y":0,"width":800,"height":600}}""")]
    [InlineData("""{"window":{"y":0,"width":800,"height":600}}""")]
    [InlineData("""{"window":"big"}""")]
    [InlineData("""{"window":null}""")]
    public void A_saved_position_that_makes_no_sense_is_ignored(string json)
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("ui.json"), json);

        Assert.Null(new UiState(dir.File("ui.json")).Window);
    }

    [Fact]
    public void The_settings_are_kept_next_to_each_other()
    {
        using var dir = new TempDir();
        var state = new UiState(dir.File("ui.json"));

        state.LastTab = "lidarr";
        state.CloseToTray = false;
        state.Window = new WindowBounds(10, 20, 900, 700, false);

        var reopened = new UiState(dir.File("ui.json"));
        Assert.Equal("lidarr", reopened.LastTab);
        Assert.False(reopened.CloseToTray);
        Assert.Equal(new WindowBounds(10, 20, 900, 700, false), reopened.Window);
    }
}

public class AppPathsTests
{
    [Fact]
    public void Every_path_lives_under_the_root()
    {
        using var dir = new TempDir();
        var paths = new AppPaths(dir.Root);

        foreach (var path in new[] { paths.ConfigFile, paths.UiStateFile, paths.CoverCache, paths.WebViewData, paths.LogFile })
        {
            Assert.StartsWith(paths.Root, path);
        }

        Assert.Equal("config.dat", Path.GetFileName(paths.ConfigFile));
    }

    [Fact]
    public void Downloads_live_under_the_root_unless_told_otherwise()
    {
        using var dir = new TempDir();
        var inRoot = Path.Combine(Path.GetFullPath(dir.Root), "Downloads");

        Assert.Equal(inRoot, new AppPaths(dir.Root).Downloads);
        Assert.Equal(inRoot, new AppPaths(dir.Root, "  ").Downloads);
        Assert.Equal(dir.File("elsewhere"), new AppPaths(dir.Root, dir.File("elsewhere")).Downloads);
    }

    [Fact]
    public void Downloads_are_always_an_absolute_path()
    {
        var paths = new AppPaths(Path.Combine("relative", "root"), Path.Combine("relative", "downloads"));

        Assert.True(Path.IsPathFullyQualified(paths.Root));
        Assert.True(Path.IsPathFullyQualified(paths.Downloads));
    }

    [Fact]
    public void The_downloads_folder_of_windows_is_an_absolute_path()
    {
        Assert.True(Path.IsPathFullyQualified(KnownFolders.Downloads()));
    }

    [Fact]
    public void The_environment_can_move_the_downloads_folder()
    {
        var rootBefore = Environment.GetEnvironmentVariable(AppPaths.RootVariable);
        var downloadsBefore = Environment.GetEnvironmentVariable(AppPaths.DownloadsVariable);
        try
        {
            using var dir = new TempDir();
            Environment.SetEnvironmentVariable(AppPaths.RootVariable, dir.File("data"));

            Environment.SetEnvironmentVariable(AppPaths.DownloadsVariable, dir.File("mine"));
            Assert.Equal(dir.File("mine"), AppPaths.FromEnvironment().Downloads);

            // Without a (usable) variable it is the Downloads folder of the Windows user, not a folder of the app.
            var windows = Path.GetFullPath(KnownFolders.Downloads());
            Environment.SetEnvironmentVariable(AppPaths.DownloadsVariable, "");
            Assert.Equal(windows, AppPaths.FromEnvironment().Downloads);

            Environment.SetEnvironmentVariable(AppPaths.DownloadsVariable, null);
            Assert.Equal(windows, AppPaths.FromEnvironment().Downloads);
            Assert.Equal(Path.GetFullPath(dir.File("data")), AppPaths.FromEnvironment().Root);
        }
        finally
        {
            Environment.SetEnvironmentVariable(AppPaths.RootVariable, rootBefore);
            Environment.SetEnvironmentVariable(AppPaths.DownloadsVariable, downloadsBefore);
        }
    }

    [Fact]
    public void The_environment_can_move_the_data_folder()
    {
        var before = Environment.GetEnvironmentVariable(AppPaths.RootVariable);
        try
        {
            using var dir = new TempDir();
            Environment.SetEnvironmentVariable(AppPaths.RootVariable, dir.Root);
            Assert.Equal(Path.GetFullPath(dir.Root), AppPaths.FromEnvironment().Root);
            Assert.True(AppPaths.FromEnvironment().IsCustomRoot);

            Environment.SetEnvironmentVariable(AppPaths.RootVariable, null);
            var expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Arr411");
            Assert.Equal(expected, AppPaths.FromEnvironment().Root);
            Assert.False(AppPaths.FromEnvironment().IsCustomRoot);
        }
        finally
        {
            Environment.SetEnvironmentVariable(AppPaths.RootVariable, before);
        }
    }
}
