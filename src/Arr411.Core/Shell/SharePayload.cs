using System.Text.Json.Nodes;
using Arr411.Core.Files;

namespace Arr411.Core.Shell;

/// <summary>The messages the window hands to the web app (what <c>App.handleShared</c> takes).</summary>
public static class SharePayload
{
    /// <summary>Text to act on: magnet links (one or several), links, a title to search.</summary>
    public static JsonObject Text(string text) => new() { ["text"] = text };

    /// <summary>.torrent files to add to qBittorrent.</summary>
    public static JsonObject Files(IEnumerable<TorrentFile> files)
    {
        var array = new JsonArray();
        foreach (var file in files) array.Add(file.ToJson());
        return new JsonObject { ["files"] = array };
    }

    /// <summary>The settings page, at a section (a tab name) when given.</summary>
    public static JsonObject Settings(string? section = null)
    {
        var payload = new JsonObject { ["tab"] = Tabs.Settings };
        if (section is not null) payload["section"] = section;
        return payload;
    }
}
