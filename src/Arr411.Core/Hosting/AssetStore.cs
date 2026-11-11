namespace Arr411.Core.Hosting;

/// <summary>Serves static files (the embedded web app) from a lookup function, like the Android app's assets.</summary>
public sealed class AssetStore
{
    private static readonly IReadOnlyDictionary<string, string> NoCache = new Dictionary<string, string>
    {
        ["Cache-Control"] = "no-cache",
    };

    private readonly Func<string, Stream?> _open;

    /// <param name="open">Opens the file at a relative path ('index.html', 'sub/x.js'), or returns null when it does not exist.</param>
    public AssetStore(Func<string, Stream?> open) => _open = open;

    public HostResponse Get(string relativePath)
    {
        var rel = relativePath.Length == 0 ? "index.html" : relativePath;
        if (rel.Contains("..", StringComparison.Ordinal)) return HostResponse.Empty(404, "Not Found");

        var body = _open(rel);
        if (body is null) return HostResponse.Empty(404, "Not Found");

        var mime = MimeTypes.For(rel);
        return new HostResponse
        {
            Status = 200,
            Reason = "OK",
            ContentType = MimeTypes.IsText(mime) ? mime + "; charset=UTF-8" : mime,
            Body = body,
            Headers = NoCache,
        };
    }
}
