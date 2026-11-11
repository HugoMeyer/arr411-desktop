namespace Arr411.Core.Shell;

public enum LinkKind
{
    /// <summary>Not something the app opens (file:, ms-msdt:, javascript:, malformed...).</summary>
    Rejected,

    /// <summary>A page of c411.org: shown in the C411 tab.</summary>
    C411,

    /// <summary>A magnet link: handed to the web app, which offers to add it to qBittorrent.</summary>
    Magnet,

    /// <summary>Any other web page: opened in the default browser.</summary>
    Web,
}

/// <summary>Decides what happens to a link that leaves a view (the Android app's openExternal), refusing anything dangerous.</summary>
public static class ExternalLinks
{
    public static LinkKind Classify(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return LinkKind.Rejected;

        return uri.Scheme switch
        {
            "magnet" => LinkKind.Magnet,
            "http" or "https" => IsC411Host(uri.Host) ? LinkKind.C411 : LinkKind.Web,
            _ => LinkKind.Rejected,
        };
    }

    public static bool IsC411Host(string host) =>
        host.Equals("c411.org", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".c411.org", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The address the C411 tab loads for a link to c411.org, or null when the link is not for it. Only the real site qualifies:
    /// no user name (<c>https://c411.org@evil.example</c> tricks), no other port, and an http link is upgraded to https.
    /// </summary>
    public static Uri? C411Page(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme is not ("http" or "https") || !IsC411Host(uri.Host) || uri.UserInfo.Length > 0 || !uri.IsDefaultPort) return null;

        return uri.Scheme == Uri.UriSchemeHttps ? uri : new UriBuilder(uri) { Scheme = Uri.UriSchemeHttps, Port = -1 }.Uri;
    }
}
