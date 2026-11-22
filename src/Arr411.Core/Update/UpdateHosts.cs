namespace Arr411.Core.Update;

/// <summary>Where an update may come from: the app only talks to GitHub, over https, and follows no redirect that leaves it.</summary>
internal static class UpdateHosts
{
    public const int MaxRedirects = 5;

    private static readonly string[] Allowed =
    [
        "api.github.com",
        "github.com",
        "objects.githubusercontent.com",
        "release-assets.githubusercontent.com",
    ];

    /// <summary>An https address of one of the GitHub hosts, on the usual port and without a user name (<c>https://github.com@evil.example</c> tricks).</summary>
    public static bool IsAllowed(Uri? uri) =>
        uri is { IsAbsoluteUri: true, Scheme: "https", IsDefaultPort: true }
        && uri.UserInfo.Length == 0
        && Allowed.Contains(uri.Host, StringComparer.OrdinalIgnoreCase);

    /// <summary>The host of an address, for the log: the rest of a download address (signed, with tokens) is never written there.</summary>
    public static string HostOf(Uri uri) => uri.IsAbsoluteUri ? uri.Host : "?";

    public static bool IsRedirect(System.Net.HttpStatusCode status) =>
        status is System.Net.HttpStatusCode.MovedPermanently
            or System.Net.HttpStatusCode.Found
            or System.Net.HttpStatusCode.SeeOther
            or System.Net.HttpStatusCode.TemporaryRedirect
            or System.Net.HttpStatusCode.PermanentRedirect;
}
