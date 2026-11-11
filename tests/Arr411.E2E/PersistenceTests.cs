using System.Text;
using System.Text.Json.Nodes;

namespace Arr411.E2E;

/// <summary>The settings are kept on disk, encrypted, and come back after a reload and after a restart.</summary>
[Trait("Category", "E2E")]
public sealed class PersistenceTests
{
    private const string Key = "S3cr3t-key-ABC123";

    private static Task<JsonObject> WaitForSavedAsync(AppProcess app, string key, string expected) =>
        app.WaitForStoredConfigAsync(stored => stored[key]?.GetValue<string>() == expected, $"{key} = {expected}");

    private static bool HoldsText(byte[] data, string text) =>
        data.AsSpan().IndexOf(Encoding.UTF8.GetBytes(text)) >= 0 || data.AsSpan().IndexOf(Encoding.Unicode.GetBytes(text)) >= 0;

    [Fact]
    public async Task Settings_are_encrypted_on_disk_and_survive_a_reload()
    {
        await using var app = await AppProcess.StartAsync();

        await app.Page.EvaluateAsync($$"""App.saveCfg({ sonarrUrl: 'http://nas:8989', sonarrKey: {{Js.Quote(Key)}} })""");

        var stored = await WaitForSavedAsync(app, "sonarrKey", Key);
        Assert.Equal("http://nas:8989", stored["sonarrUrl"]?.GetValue<string>());

        // Nothing readable in the file itself.
        Assert.False(HoldsText(File.ReadAllBytes(app.ConfigFile), Key));

        await app.ReloadAsync();
        Assert.Equal(Key, await app.Page.EvaluateAsync<string>("App.loadCfg().sonarrKey"));
    }

    [Fact]
    public async Task Settings_survive_a_restart()
    {
        var dataDir = AppProcess.NewDataDir();
        try
        {
            await using (var first = await AppProcess.StartAsync(dataDir: dataDir))
            {
                await first.Page.EvaluateAsync($$"""App.saveCfg({ sonarrUrl: 'http://nas:8989', sonarrKey: {{Js.Quote(Key)}} })""");
                await WaitForSavedAsync(first, "sonarrKey", Key);
                Assert.Equal(0, await first.CloseAsync());
            }

            await using var second = await AppProcess.StartAsync(dataDir: dataDir);

            Assert.Equal(Key, await second.Page.EvaluateAsync<string>("App.loadCfg().sonarrKey"));
            Assert.Equal("http://nas:8989", await second.Page.EvaluateAsync<string>("App.loadCfg().sonarrUrl"));

            // With a service set up, the app no longer starts on the settings.
            Assert.NotEqual("settings", await second.Page.EvaluateAsync<string>("App.state.tab"));
        }
        finally
        {
            AppProcess.TryDeleteDirectory(dataDir);
        }
    }

    [Fact]
    public async Task A_key_is_nowhere_in_clear_on_disk()
    {
        var dataDir = AppProcess.NewDataDir();
        try
        {
            await using (var app = await AppProcess.StartAsync(dataDir: dataDir))
            {
                await app.Page.EvaluateAsync($$"""App.saveCfg({ sonarrUrl: 'http://nas:8989', sonarrKey: {{Js.Quote(Key)}} })""");
                await WaitForSavedAsync(app, "sonarrKey", Key);
                await app.CloseAsync();
            }

            // Settings, backup copy, web view profile, log: everything the app left behind.
            var leaks = new List<string>();
            foreach (var file in Directory.EnumerateFiles(dataDir, "*", SearchOption.AllDirectories))
            {
                try
                {
                    using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var content = new MemoryStream();
                    stream.CopyTo(content);
                    if (HoldsText(content.ToArray(), Key)) leaks.Add(Path.GetRelativePath(dataDir, file));
                }
                catch (IOException)
                {
                    // Locked by a process that is still shutting down: nothing to learn from it.
                }
            }

            Assert.Empty(leaks);
        }
        finally
        {
            AppProcess.TryDeleteDirectory(dataDir);
        }
    }
}
