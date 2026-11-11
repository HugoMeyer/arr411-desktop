using System.Text.Json.Nodes;

namespace Arr411.Core.Files;

/// <summary>A .torrent file could not be used; the message is meant for the user.</summary>
public sealed class TorrentFileException(string message) : Exception(message);

/// <summary>A .torrent file read from the disk, ready to be handed to the web app (same checks as the Android app).</summary>
public sealed class TorrentFile
{
    /// <summary>The biggest file accepted.</summary>
    public const int MaxBytes = 20 * 1024 * 1024;

    private TorrentFile(string name, byte[] data)
    {
        Name = name;
        Data = data;
    }

    /// <summary>The file name, always ending in .torrent.</summary>
    public string Name { get; }

    public byte[] Data { get; }

    /// <summary>The shape the web app expects: <c>{ name, base64 }</c>.</summary>
    public JsonObject ToJson() => new() { ["name"] = Name, ["base64"] = Convert.ToBase64String(Data) };

    /// <summary>Blocking: call it off the UI thread.</summary>
    /// <exception cref="TorrentFileException">The file is unreadable, too big or not a .torrent.</exception>
    public static TorrentFile Read(string path)
    {
        var name = DisplayName(path);
        byte[] data;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
            if (stream.Length > MaxBytes) throw new TorrentFileException($"fichier trop gros ({name})");

            data = new byte[stream.Length];
            stream.ReadExactly(data);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            throw new TorrentFileException($"fichier illisible ({name})");
        }

        return FromBytes(name, data);
    }

    /// <exception cref="TorrentFileException">Too big or not a .torrent.</exception>
    public static TorrentFile FromBytes(string name, byte[] data)
    {
        name = string.IsNullOrWhiteSpace(name) ? "fichier.torrent" : name;
        if (data.Length > MaxBytes) throw new TorrentFileException($"fichier trop gros ({name})");

        // A .torrent is a bencoded dictionary: it always starts with 'd'.
        if (data.Length == 0 || data[0] != (byte)'d') throw new TorrentFileException($"« {name} » n’est pas un fichier .torrent");

        if (!name.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase)) name += ".torrent";
        return new TorrentFile(name, data);
    }

    private static string DisplayName(string path)
    {
        var name = Path.GetFileName(path);
        return string.IsNullOrWhiteSpace(name) ? "fichier.torrent" : name;
    }
}
