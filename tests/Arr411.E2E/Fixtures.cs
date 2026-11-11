using System.Text.Json.Nodes;

namespace Arr411.E2E;

/// <summary>An app started with no settings at all, as on a first run.</summary>
public sealed class FirstRunApp : IAsyncLifetime
{
    public AppProcess App { get; private set; } = null!;

    public async Task InitializeAsync() => App = await AppProcess.StartAsync();

    public async Task DisposeAsync()
    {
        if (App is not null) await App.DisposeAsync();
    }
}

/// <summary>An app whose C411 tab shows a stand-in for the site, with a stand-in for qBittorrent to send the torrents to.</summary>
public sealed class C411App : IAsyncLifetime
{
    public FakeServices Fake { get; private set; } = null!;

    public FakeC411 Site { get; private set; } = null!;

    public AppProcess App { get; private set; } = null!;

    /// <summary>The settings of an app that sends torrents to the stand-in for qBittorrent and starts on the C411 tab.</summary>
    public static JsonObject Settings(FakeServices fake) => new()
    {
        ["qbUrl"] = fake.BaseUrl,
        ["username"] = FakeServices.QbUser,
        ["password"] = FakeServices.QbPassword,
        ["startTab"] = "c411",
    };

    public async Task InitializeAsync()
    {
        Fake = await FakeServices.StartAsync();
        try
        {
            Site = await FakeC411.StartAsync();
            App = await AppProcess.StartAsync(Settings(Fake), site: Site);
        }
        catch
        {
            await DisposeAsync();
            throw;
        }
    }

    public async Task DisposeAsync()
    {
        if (App is not null) await App.DisposeAsync();
        if (Site is not null) await Site.DisposeAsync();
        if (Fake is not null) await Fake.DisposeAsync();
    }
}

/// <summary>An app set up for a Radarr, which a local stand-in answers.</summary>
public class ConfiguredApp : IAsyncLifetime
{
    public FakeServices Fake { get; private set; } = null!;

    public AppProcess App { get; private set; } = null!;

    /// <summary>How many films the stand-in for Radarr lists.</summary>
    protected virtual int Movies => 1;

    public async Task InitializeAsync()
    {
        Fake = await FakeServices.StartAsync(Movies);
        try
        {
            App = await AppProcess.StartAsync(new JsonObject
            {
                ["radarrUrl"] = Fake.BaseUrl,
                ["radarrKey"] = FakeServices.ApiKey,
                ["startTab"] = "radarr",
            });
        }
        catch
        {
            await Fake.DisposeAsync();
            throw;
        }
    }

    public async Task DisposeAsync()
    {
        if (App is not null) await App.DisposeAsync();
        if (Fake is not null) await Fake.DisposeAsync();
    }
}

/// <summary>The same app with a library that fills even a very wide window many times over.</summary>
public sealed class LibraryApp : ConfiguredApp
{
    public const int FilmCount = 300;

    protected override int Movies => FilmCount;
}

/// <summary>An app with every service set up (stand-ins answer for them and for C411), on the Radarr tab: the window the shell tests use.</summary>
public sealed class ShellApp : IAsyncLifetime
{
    public FakeServices Fake { get; private set; } = null!;

    public FakeC411 Site { get; private set; } = null!;

    public AppProcess App { get; private set; } = null!;

    public static JsonObject Settings(FakeServices fake) => new()
    {
        ["radarrUrl"] = fake.BaseUrl,
        ["radarrKey"] = FakeServices.ApiKey,
        ["sonarrUrl"] = fake.BaseUrl,
        ["sonarrKey"] = FakeServices.ApiKey,
        ["lidarrUrl"] = fake.BaseUrl,
        ["lidarrKey"] = FakeServices.ApiKey,
        ["qbUrl"] = fake.BaseUrl,
        ["username"] = FakeServices.QbUser,
        ["password"] = FakeServices.QbPassword,
        ["startTab"] = "radarr",
    };

    public async Task InitializeAsync()
    {
        Fake = await FakeServices.StartAsync();
        try
        {
            Site = await FakeC411.StartAsync();
            App = await AppProcess.StartAsync(Settings(Fake), site: Site);
        }
        catch
        {
            await DisposeAsync();
            throw;
        }
    }

    public async Task DisposeAsync()
    {
        if (App is not null) await App.DisposeAsync();
        if (Site is not null) await Site.DisposeAsync();
        if (Fake is not null) await Fake.DisposeAsync();
    }
}
