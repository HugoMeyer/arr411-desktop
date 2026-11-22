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
        if (SiteAddress(url) is not { } uri) return null;

        return uri.Scheme == Uri.UriSchemeHttps ? uri : new UriBuilder(uri) { Scheme = Uri.UriSchemeHttps, Port = -1 }.Uri;
    }

    /// <summary>
    /// Whether a message that reached the C411 tab was sent by the page on screen: both addresses are pages of c411.org over https (see
    /// <see cref="C411Page"/>, without the upgrade: a message of a clear-text page is refused) and of the same host. Another subdomain of
    /// c411.org, or another site, is not the page the user is looking at.
    /// </summary>
    /// <param name="messageSource">The address of the document that sent the message.</param>
    /// <param name="shownPage">The address of the page on screen.</param>
    public static bool IsMessageFromShownPage(string? messageSource, string? shownPage) =>
        SiteAddress(messageSource) is { Scheme: "https" } source
        && SiteAddress(shownPage) is { Scheme: "https" } shown
        && source.Host.Equals(shown.Host, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// What Windows is asked to open: the address as the framework writes it (trimmed, escaped), for a web page or the page of Windows Settings, and null
    /// for anything else. The shell takes a text that is no address for a program to start.
    /// </summary>
    public static string? ShellTarget(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return null;

        return uri.Scheme is "http" or "https" or "ms-settings" ? uri.AbsoluteUri : null;
    }

    // The address of the real site, as it was written: an http or https one of c411.org, with no user name and no other port.
    private static Uri? SiteAddress(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return null;

        return uri.Scheme is "http" or "https" && IsC411Host(uri.Host) && uri.UserInfo.Length == 0 && uri.IsDefaultPort ? uri : null;
    }
}
