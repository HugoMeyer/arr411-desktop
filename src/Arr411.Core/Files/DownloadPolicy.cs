using System.Text;

namespace Arr411.Core.Files;

/// <summary>
/// What the C411 page may put in the Downloads folder: files of a few harmless kinds, under a name it cannot use to reach
/// another folder or a Windows device, and never over an existing file. The browser does the writing, and marks what it writes as coming
/// from the Internet (SmartScreen, Protected View): the page never hands over the bytes of a file itself.
/// </summary>
public static class DownloadPolicy
{
    private const int MaxNameLength = 120;
    private const int MaxExtensionLength = 16;
    private const int MaxAttempts = 1000;
    private const string FallbackName = "fichier";

    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".torrent", ".nfo", ".txt", ".srt", ".sub", ".zip", ".rar", ".7z", ".jpg", ".jpeg", ".png", ".gif", ".webp", ".pdf",
    };

    // Opening such a name writes to a device, not to a file (the superscript digits count too).
    private static readonly HashSet<string> DeviceNames = BuildDeviceNames();

    private static HashSet<string> BuildDeviceNames()
    {
        var names = new HashSet<string>(["CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$"], StringComparer.OrdinalIgnoreCase);
        foreach (var digit in "0123456789¹²³")
        {
            names.Add("COM" + digit);
            names.Add("LPT" + digit);
        }

        return names;
    }

    /// <summary>
    /// A file name that is safe to create in a folder of ours: no folder part, no character Windows refuses,
    /// no trailing dot or space, no device name, not too long. Never empty.
    /// </summary>
    public static string SafeName(string? name)
    {
        var text = name ?? "";
        text = text[(text.LastIndexOfAny(['/', '\\']) + 1)..];

        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(text.Length);
        foreach (var c in text) builder.Append(char.IsControl(c) || Array.IndexOf(invalid, c) >= 0 ? '_' : c);

        // Windows drops trailing dots and spaces, which would let "x.exe." pass for something else than it is.
        var clean = builder.ToString().Trim().TrimEnd('.', ' ');
        var extension = Path.GetExtension(clean);
        if (extension.Length > MaxExtensionLength) extension = "";

        var stem = clean[..^extension.Length];
        var room = MaxNameLength - extension.Length;
        if (stem.Length > room)
        {
            // Half of a surrogate pair is not a valid name.
            stem = stem[..(char.IsHighSurrogate(stem[room - 1]) ? room - 1 : room)];
        }

        stem = stem.TrimEnd('.', ' ');
        if (stem.Length == 0) stem = FallbackName;
        if (DeviceNames.Contains(stem.Split('.')[0].TrimEnd())) stem = "_" + stem;

        return stem + extension;
    }

    /// <summary>Whether a file of this name may be saved (decided on the extension, as the browsers do).</summary>
    public static bool IsAllowed(string fileName) => AllowedExtensions.Contains(Path.GetExtension(fileName));

    /// <summary>
    /// Creates an empty file under a name that is free in the folder ("name.ext", then "name (1).ext"...) and returns its path.
    /// The file exists from now on, so that two downloads of the same name cannot be given the same path (the browser overwrites).
    /// </summary>
    public static string Reserve(string folder, string fileName)
    {
        using var stream = CreateNew(folder, fileName, out var path);
        return path;
    }

    /// <summary>Removes the file when nothing was written to it (a reserved name whose download did not happen).</summary>
    public static void DiscardIfEmpty(string path)
    {
        try
        {
            var file = new FileInfo(path);
            if (file.Exists && file.Length == 0) file.Delete();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Busy or already gone.
        }
    }

    private static FileStream CreateNew(string folder, string fileName, out string path)
    {
        Directory.CreateDirectory(folder);
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            path = Path.Combine(folder, Candidate(fileName, attempt));
            try
            {
                return new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && (File.Exists(path) || Directory.Exists(path)))
            {
                // Taken (a folder of that name gives "access denied"), possibly just now: the next name.
            }
        }

        throw new IOException("Trop de fichiers du même nom");
    }

    private static string Candidate(string fileName, int attempt)
    {
        if (attempt == 0) return fileName;

        var extension = Path.GetExtension(fileName);
        return $"{fileName[..^extension.Length]} ({attempt}){extension}";
    }
}
