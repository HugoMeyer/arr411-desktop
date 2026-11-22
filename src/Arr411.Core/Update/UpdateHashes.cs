namespace Arr411.Core.Update;

/// <summary>The hash sources of a release: the digest GitHub computes for every asset, and the optional SHA256SUMS.txt asset.</summary>
internal static class UpdateHashes
{
    /// <summary>The most bytes of a SHA256SUMS.txt that are read: a line per asset is under 100 bytes.</summary>
    public const int MaxSumsBytes = 64 * 1024;

    private const string DigestPrefix = "sha256:";

    /// <summary>The hex of a digest written <c>sha256:&lt;64 hex&gt;</c>, in lower case; null when it is missing, malformed, or of another algorithm.</summary>
    public static string? ParseDigest(string? digest)
    {
        if (digest is null || !digest.StartsWith(DigestPrefix, StringComparison.OrdinalIgnoreCase)) return null;

        var hex = digest[DigestPrefix.Length..];
        return IsSha256(hex) ? hex.ToLowerInvariant() : null;
    }

    public static bool IsSha256(string text) => text.Length == 64 && text.All(char.IsAsciiHexDigit);

    /// <summary>
    /// The hash a SHA256SUMS text gives for a file, in lower case: the lines are <c>&lt;64 hex&gt;  &lt;file name&gt;</c>, with a <c>*</c> before the name when the file
    /// was read in binary mode, and CRLF or LF ends. Null when the file has no line, or two lines that disagree: neither is something to install by.
    /// </summary>
    public static string? FindInSums(string? text, string fileName)
    {
        if (string.IsNullOrEmpty(text)) return null;

        string? found = null;
        foreach (var raw in text.TrimStart('\uFEFF').Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length < 66 || !IsSha256(line[..64]) || line[64] is not (' ' or '\t')) continue;

            var name = line[64..].TrimStart(' ', '\t');
            if (name.StartsWith('*')) name = name[1..];
            if (!name.Equals(fileName, StringComparison.OrdinalIgnoreCase)) continue;

            var hash = line[..64].ToLowerInvariant();
            if (found is not null && found != hash) return null;

            found = hash;
        }

        return found;
    }
}
