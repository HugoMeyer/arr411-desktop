using System.Globalization;

namespace Arr411.Core.Update;

/// <summary>Release versions ("0.3.0", "v0.3.0", "1.2.3.4"): one to four dotted numbers, compared number by number.</summary>
internal static class UpdateVersion
{
    private const int Numbers = 4;
    private const int MaxDigits = 9;

    /// <summary>The numbers of a version (the "v" is optional, missing ones are zero), or null for anything else: "0.3.0-beta" or "latest" are not versions.</summary>
    public static int[]? Parse(string? text)
    {
        var clean = Clean(text);
        if (clean is null) return null;

        var pieces = clean.Split('.');
        var numbers = new int[Numbers];
        for (var i = 0; i < pieces.Length; i++) numbers[i] = int.Parse(pieces[i], CultureInfo.InvariantCulture);
        return numbers;
    }

    /// <summary>The version as it is shown, without the "v" of a tag ("v0.3.0" gives "0.3.0"); null when the text is not a version.</summary>
    public static string? Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var clean = text.Trim();
        if (clean[0] is 'v' or 'V') clean = clean[1..];

        var pieces = clean.Split('.');
        if (pieces.Length > Numbers) return null;

        foreach (var piece in pieces)
        {
            if (piece.Length is 0 or > MaxDigits || !piece.All(char.IsAsciiDigit)) return null;
        }

        return clean;
    }

    public static int Compare(int[] left, int[] right)
    {
        for (var i = 0; i < Numbers; i++)
        {
            if (left[i] != right[i]) return left[i] < right[i] ? -1 : 1;
        }

        return 0;
    }

    /// <summary>True when <paramref name="candidate"/> is strictly above <paramref name="current"/>; false when it is not, or when either is not a version.</summary>
    public static bool IsNewer(string? candidate, string? current) =>
        Parse(candidate) is { } a && Parse(current) is { } b && Compare(a, b) > 0;
}
