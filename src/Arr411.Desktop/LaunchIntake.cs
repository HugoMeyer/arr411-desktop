using System.Runtime.InteropServices;
using Arr411.Core.Files;
using Arr411.Core.Logging;
using Arr411.Core.Shell;

namespace Arr411.Desktop;

/// <summary>
/// What the outside world hands to the window: the arguments of a launch of the program (this one's, or another one's, which the single-instance
/// listener forwards), and what is dropped on the window or on a page. What reaches it before the window has a handle waits for it.
/// </summary>
internal sealed class LaunchIntake
{
    private readonly IMainWindow _window;
    private readonly WebHost _web;

    // What reaches the window before it has a handle (the request this very launch comes with) waits here.
    private readonly EarlyQueue<LaunchRequest> _early = new();
    private bool _dropAccepted;

    public LaunchIntake(IMainWindow window, WebHost web)
    {
        _window = window;
        _web = web;
    }

    /// <summary>Hands the window a launch of the program, from this process or another one. May be called from any thread.</summary>
    public void Post(LaunchRequest request)
    {
        if (_early.TryHold(request)) return;

        try
        {
            _window.Post(() => Run(request));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            // The window is closing: nobody is left to take it.
        }
    }

    /// <summary>The window has a handle: what waited for it is run now, in the order it came.</summary>
    public void Open()
    {
        foreach (var request in _early.Release()) _window.Post(() => Run(request));
    }

    public DragDropEffects DragEnter(IDataObject? data)
    {
        _dropAccepted = ParseDrop(data).HasWork;
        return DragOver();
    }

    public DragDropEffects DragOver() => _dropAccepted ? DragDropEffects.Copy : DragDropEffects.None;

    public void DragLeave() => _dropAccepted = false;

    public void Drop(IDataObject? data)
    {
        _dropAccepted = false;
        var request = ParseDrop(data);
        if (request.HasWork) Post(request);
    }

    // The web app reported a drop. The window does what it does for a drop on its navigation bar, except that the cursor could not refuse it:
    // what is not usable is said here.
    public void DroppedOnApp(DroppedContent content)
    {
        if (_window.IsClosed) return;

        try
        {
            if (content.Request.HasWork) Run(content.Request);
            if (content.Files.Count > 0) _web.Bridge.Share(SharePayload.Files(content.Files));

            if (content.Notice is { } notice) _window.Toast(notice);
        }
        catch (Exception ex)
        {
            Log.Error("A drop on a page failed", ex);
        }
    }

    // The C411 page reported a drop (the user's, as far as the bridge can tell). The window hands it over as it does for the web app, but quietly:
    // a page does not get the window to come to the front, nor the tab to change (DroppedOnApp is for the web app, which is the app's own).
    public void DroppedOnPage(DroppedContent content)
    {
        if (_window.IsClosed) return;

        try
        {
            if (content.Request.C411Url is { } url) _web.C411.Navigate(url);
            if (content.Request.Magnets.Count > 0) _web.Bridge.Share(SharePayload.Magnets(content.Request.Magnets));
            if (content.Files.Count > 0) _web.Bridge.Share(SharePayload.Files(content.Files));

            if (content.Notice is { } notice) _window.Toast(notice);
        }
        catch (Exception ex)
        {
            Log.Error("A drop on a page failed", ex);
        }
    }

    private void Run(LaunchRequest request)
    {
        if (_window.IsClosed) return;

        try
        {
            if (request.Quit)
            {
                _window.Quit();
                return;
            }

            if (request.Ignored > 0) Log.Warn("Some arguments of a launch were ignored: " + request.Ignored);
            if (request.ShowsWindow) _window.ShowWindow();
            if (request.C411Url is { } url)
            {
                _web.C411.Navigate(url);
                _window.ShowTab(Tabs.C411, tellSpa: true);
            }

            if (request.Tab is { } tab) _window.ShowTab(tab, tellSpa: true);
            if (request.Magnets.Count > 0) _web.Bridge.Share(SharePayload.Magnets(request.Magnets));
            if (request.TorrentFiles.Count > 0) _ = ShareFilesAsync(request.TorrentFiles);
        }
        catch (Exception ex)
        {
            Log.Error("A launch request failed", ex);
        }
    }

    private async Task ShareFilesAsync(IReadOnlyList<string> paths)
    {
        try
        {
            var files = new List<TorrentFile>();
            foreach (var path in paths)
            {
                try
                {
                    files.Add(await Task.Run(() => TorrentFile.Read(path)));
                }
                catch (TorrentFileException ex)
                {
                    if (!_window.IsClosed) _window.Toast(ex.Message);
                }
            }

            if (files.Count > 0 && !_window.IsClosed) _web.Bridge.Share(SharePayload.Files(files));
        }
        catch (Exception ex)
        {
            Log.Error("Sharing torrent files failed", ex);
        }
    }

    private static LaunchRequest ParseDrop(IDataObject? data)
    {
        try
        {
            // The paths of files come from Explorer: all of them count, and none is taken for an option.
            if (data?.GetDataPresent(DataFormats.FileDrop) == true && data.GetData(DataFormats.FileDrop) is string[] files)
            {
                return LaunchRequest.FromDroppedFiles(files);
            }

            if (data?.GetDataPresent(DataFormats.UnicodeText) == true && data.GetData(DataFormats.UnicodeText) is string text)
            {
                return LaunchRequest.FromText(text);
            }
        }
        catch (Exception ex) when (ex is ExternalException or InvalidCastException or InvalidOperationException or ArgumentException or OutOfMemoryException)
        {
            Log.Warn("A drop could not be read: " + ex.GetType().Name);
        }

        return LaunchRequest.Empty;
    }
}
