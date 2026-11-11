using System.Text.Json;
using Arr411.Core.Files;

namespace Arr411.Core.Shell;

/// <summary>
/// What a page says was dropped on it (see Scripts\drop-adapter.js): some text and some .torrent files. A page is not trusted, so all of it is
/// checked here, with the same rules as for a drop on the navigation bar. Nothing is added to qBittorrent by itself: the window hands it to the
/// web app, which asks the user first.
/// </summary>
public sealed class DroppedContent
{
    /// <summary>How many files one drop may carry.</summary>
    public const int MaxFiles = LaunchRequest.MaxItems;

    /// <summary>The most bytes of files one drop may carry.</summary>
    public const long MaxTotalBytes = 2L * TorrentFile.MaxBytes;

    public static readonly DroppedContent Empty = new(LaunchRequest.Empty, [], []);

    private DroppedContent(LaunchRequest request, IReadOnlyList<TorrentFile> files, IReadOnlyList<string> problems)
    {
        Request = request;
        Files = files;
        Problems = problems;
    }

    /// <summary>What the text asks for: magnet links, a page of c411.org. Never a file.</summary>
    public LaunchRequest Request { get; }

    /// <summary>The .torrent files that were found valid.</summary>
    public IReadOnlyList<TorrentFile> Files { get; }

    /// <summary>What was refused, for the user (in French).</summary>
    public IReadOnlyList<string> Problems { get; }

    /// <summary>There is something to hand to the web app or to show.</summary>
    public bool HasWork => Request.HasWork || Files.Count > 0;

    /// <param name="message">The message of the page: <c>{ t: "drop", text, files: [{ name, base64 }] }</c>, everything optional.</param>
    public static DroppedContent Parse(JsonElement message) => Parse(message, MaxTotalBytes);

    internal static DroppedContent Parse(JsonElement message, long maxTotalBytes)
    {
        var request = LaunchRequest.FromText(JsonText.Str(message, "text"));
        var files = new List<TorrentFile>();
        var problems = new List<string>();

        if (message.ValueKind == JsonValueKind.Object && message.TryGetProperty("files", out var entries) && entries.ValueKind == JsonValueKind.Array)
        {
            long total = 0;
            var seen = 0;
            foreach (var entry in entries.EnumerateArray())
            {
                if (++seen > MaxFiles)
                {
                    problems.Add($"Trop de fichiers : {MaxFiles} au maximum");
                    break;
                }

                if (Read(entry, maxTotalBytes, ref total, problems) is { } file) files.Add(file);
            }
        }

        return new DroppedContent(request, files, problems);
    }

    private static TorrentFile? Read(JsonElement entry, long maxTotalBytes, ref long total, List<string> problems)
    {
        // The name is only shown, but it is still the page's: no folder part, no control character.
        var name = DownloadPolicy.SafeName(JsonText.Str(entry, "name"));
        if (!name.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase))
        {
            problems.Add($"« {name} » n’est pas un fichier .torrent");
            return null;
        }

        if (JsonText.Str(entry, "base64") is not { } base64)
        {
            problems.Add($"fichier illisible ({name})");
            return null;
        }

        // The size is known from the length of the text: a file that is too big is not even decoded.
        var padding = base64.EndsWith("==", StringComparison.Ordinal) ? 2 : base64.EndsWith('=') ? 1 : 0;
        var size = (long)base64.Length / 4 * 3 - padding;
        if (size > TorrentFile.MaxBytes)
        {
            problems.Add($"fichier trop gros ({name})");
            return null;
        }

        if (total + size > maxTotalBytes)
        {
            problems.Add("Trop de données d’un coup");
            return null;
        }

        try
        {
            var file = TorrentFile.FromBytes(name, Convert.FromBase64String(base64));
            total += file.Data.Length;
            return file;
        }
        catch (FormatException)
        {
            problems.Add($"fichier illisible ({name})");
        }
        catch (TorrentFileException ex)
        {
            problems.Add(ex.Message);
        }

        return null;
    }
}
