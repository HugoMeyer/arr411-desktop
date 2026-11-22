using System.Text;

namespace Arr411.Core.Bridge;

/// <summary>
/// What the web app may have saved as a file (<c>Native.saveText</c>, the export of the settings): a bounded text, and a name that can only be the plain
/// name of a .json file. Both come from a page that shows what others wrote, so both are checked before a dialog is shown for them.
/// </summary>
internal static class SettingsExport
{
    /// <summary>The most a file may weigh, once written (UTF-8).</summary>
    public const int MaxBytes = 1024 * 1024;

    /// <summary>The longest name, with its extension.</summary>
    public const int MaxNameLength = 100;

    public const string DefaultName = "arr411-reglages.json";

    public const string TooBig = "Le fichier à enregistrer est trop volumineux.";

    private const string Extension = ".json";

    public static bool Fits(string text) => text.Length <= MaxBytes && Encoding.UTF8.GetByteCount(text) <= MaxBytes;

    /// <summary>
    /// The name to offer for the file: what follows the last separator, without what a file name cannot hold, without the names Windows keeps for its
    /// devices, at most <see cref="MaxNameLength"/> characters, and always ending in <c>.json</c>. <see cref="DefaultName"/> when nothing is left.
    /// </summary>
    public static string FileName(string? requested)
    {
        var name = requested ?? "";
        var cut = name.LastIndexOfAny(['/', '\\']);
        var stem = Clean(cut >= 0 ? name[(cut + 1)..] : name);
        if (stem.EndsWith(Extension, StringComparison.OrdinalIgnoreCase)) stem = stem[..^Extension.Length];

        stem = stem.Trim().TrimEnd('.');
        if (stem.Length == 0) return DefaultName;

        if (IsDeviceName(stem)) stem = "_" + stem;

        stem = Shorten(stem, MaxNameLength - Extension.Length).TrimEnd('.', ' ');
        return stem.Length == 0 ? DefaultName : stem + Extension;
    }

    // What a name may not hold is replaced, so that what is left of it still tells what the file is.
    private static string Clean(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var text = new StringBuilder(name.Length);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsHighSurrogate(c) && i + 1 < name.Length && char.IsLowSurrogate(name[i + 1]))
            {
                text.Append(c).Append(name[++i]);
                continue;
            }

            text.Append(char.IsSurrogate(c) || char.IsControl(c) || Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        }

        return text.ToString();
    }

    // CON, NUL, COM1... are the devices of Windows, whatever follows them (CON.json is the console).
    private static bool IsDeviceName(string stem)
    {
        var dot = stem.IndexOf('.');
        var name = (dot < 0 ? stem : stem[..dot]).TrimEnd();
        return name.Length switch
        {
            3 => name.Equals("CON", StringComparison.OrdinalIgnoreCase)
                || name.Equals("PRN", StringComparison.OrdinalIgnoreCase)
                || name.Equals("AUX", StringComparison.OrdinalIgnoreCase)
                || name.Equals("NUL", StringComparison.OrdinalIgnoreCase),
            4 => (name.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || name.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
                && name[3] is >= '1' and <= '9',
            _ => false,
        };
    }

    // Never in the middle of a character made of two surrogates.
    private static string Shorten(string text, int max)
    {
        if (text.Length <= max) return text;

        return text[..(char.IsHighSurrogate(text[max - 1]) ? max - 1 : max)];
    }
}
