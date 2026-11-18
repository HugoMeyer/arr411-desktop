using System.Globalization;

namespace Arr411.Core.Diagnostics;

internal static class Humanize
{
    public static string Megabytes(long bytes) => (bytes / (1024 * 1024)).ToString(CultureInfo.InvariantCulture);

    /// <summary>"45 s", "12 min 5 s", "3 h 20 min", "2 d 3 h": the two biggest units, the way people say it.</summary>
    public static string Duration(TimeSpan span)
    {
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        var culture = CultureInfo.InvariantCulture;
        return span.TotalHours >= 24 ? string.Create(culture, $"{span.Days} d {span.Hours} h")
            : span.TotalMinutes >= 60 ? string.Create(culture, $"{span.Hours} h {span.Minutes} min")
            : span.TotalSeconds >= 60 ? string.Create(culture, $"{span.Minutes} min {span.Seconds} s")
            : string.Create(culture, $"{span.Seconds} s");
    }
}
