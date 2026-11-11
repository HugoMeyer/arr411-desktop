using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Arr411.Core.Hosting;

/// <summary>Finds the scripts written inside an HTML page, so that its security policy allows exactly those and nothing else.</summary>
internal static partial class InlineScripts
{
    [GeneratedRegex(@"<script(?<attributes>(?:\s[^>]*)?)>(?<code>.*?)</script\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ScriptTag();

    [GeneratedRegex(@"\ssrc\s*=", RegexOptions.IgnoreCase)]
    private static partial Regex SourceAttribute();

    /// <summary>The policy sources ('sha256-...') of the inline scripts of a page, not counting the ones it loads from a file.</summary>
    public static IReadOnlyList<string> Hashes(string html)
    {
        var hashes = new List<string>();
        foreach (Match script in ScriptTag().Matches(html))
        {
            if (SourceAttribute().IsMatch(script.Groups["attributes"].Value)) continue;

            // The browser hashes the text it parsed, in which every line break is a single "\n".
            var code = script.Groups["code"].Value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
            if (code.Length == 0) continue;

            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(code));
            var source = "'sha256-" + Convert.ToBase64String(hash) + "'";
            if (!hashes.Contains(source)) hashes.Add(source);
        }

        return hashes;
    }
}
