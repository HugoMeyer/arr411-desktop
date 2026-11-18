using System.Text.RegularExpressions;

namespace Arr411.Core.Diagnostics;

/// <summary>
/// Takes what should not leave the computer out of a text that is about to be shared: passwords, tokens and cookies, the credentials inside a URL, and
/// the name of the Windows account (it is in every path of its profile). A diagnostic report is pasted into chats and issues.
/// </summary>
public static partial class Redaction
{
    private const string Hidden = "***";

    [GeneratedRegex(@"(?<key>\b(?:password|passwd|pwd|token|apikey|api[_-]key|secret|sid|session|cookie|set-cookie|authorization)\b""?\s*[=:]\s*)(?:""[^""\r\n]*""|(?:bearer|basic)\s+[^\s&;,""']+|[^\s&;,""']+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Secret();

    [GeneratedRegex(@"(?<scheme>\b[a-z][a-z0-9+.\-]*://)[^/\s:@]+(?::[^/\s@]*)?@", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlCredentials();

    public static string Clean(string text) => Clean(text, Environment.UserName, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    public static string Clean(string text, string? userName, string? profileFolder)
    {
        var clean = UrlCredentials().Replace(text, "${scheme}" + Hidden + "@");
        clean = Secret().Replace(clean, "${key}" + Hidden);

        if (!string.IsNullOrEmpty(profileFolder))
        {
            clean = clean.Replace(profileFolder, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
            clean = clean.Replace(profileFolder.Replace('\\', '/'), "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        }

        // Short names (a, me, pc) would be cut out of every other word.
        if (userName is { Length: >= 3 }) clean = Regex.Replace(clean, @"\b" + Regex.Escape(userName) + @"\b", "<user>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return clean;
    }
}
