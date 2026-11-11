using Arr411.Core.Config;
using Arr411.Core.Http;
using Arr411.Core.Logging;

namespace Arr411.Desktop;

/// <summary>Everything the window needs that is not a view: where the files are, the settings, the network client, the cover cache.</summary>
internal sealed class AppServices : IDisposable
{
    private AppServices(AppPaths paths, string version, ConfigStore config, UiState ui, NativeHttp http, ImageProxy covers, PageFetcher pageFetcher)
    {
        Paths = paths;
        Version = version;
        Config = config;
        Ui = ui;
        Http = http;
        Covers = covers;
        PageFetcher = pageFetcher;
    }

    public AppPaths Paths { get; }

    public string Version { get; }

    public ConfigStore Config { get; }

    public UiState Ui { get; }

    public NativeHttp Http { get; }

    public ImageProxy Covers { get; }

    /// <summary>The downloads the C411 page asks the app to make for it (the fallback of its own fetch).</summary>
    public PageFetcher PageFetcher { get; }

    public static AppServices Create(AppPaths paths)
    {
        Directory.CreateDirectory(paths.Root);
        Log.Initialize(paths.LogFile);

        var version = typeof(AppServices).Assembly.GetName().Version!.ToString(3);
        Log.Info("Arr411 Desktop " + version + " starting (process " + Environment.ProcessId + ")");

        var config = new ConfigStore(paths.ConfigFile, new DpapiProtector());
        var ui = new UiState(paths.UiStateFile);
        var http = new NativeHttp("Arr411-Desktop/" + version);
        var covers = new ImageProxy(config, paths.CoverCache);
        _ = Task.Run(covers.Prune);

        return new AppServices(paths, version, config, ui, http, covers, new PageFetcher());
    }

    public void Dispose()
    {
        PageFetcher.Dispose();
        Covers.Dispose();
        Http.Dispose();
    }
}
