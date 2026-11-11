namespace Arr411.Core.Hosting;

/// <summary>Content types of the files served from https://arr411.local (same table as the Android app).</summary>
public static class MimeTypes
{
    public const string Default = "application/octet-stream";

    private static readonly Dictionary<string, string> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html",
        [".js"] = "application/javascript",
        [".css"] = "text/css",
        [".svg"] = "image/svg+xml",
        [".json"] = "application/json",
        [".png"] = "image/png",
        [".webp"] = "image/webp",
        [".gif"] = "image/gif",
        [".woff2"] = "font/woff2",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
    };

    public static string For(string name) =>
        ByExtension.TryGetValue(Path.GetExtension(name), out var mime) ? mime : Default;

    /// <summary>Text types are served with a UTF-8 charset.</summary>
    public static bool IsText(string mime) =>
        mime.StartsWith("text/", StringComparison.Ordinal)
        || mime.Contains("javascript", StringComparison.Ordinal)
        || mime.Contains("json", StringComparison.Ordinal)
        || mime.Contains("svg", StringComparison.Ordinal);
}
