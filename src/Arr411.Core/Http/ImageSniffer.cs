using System.Text;

namespace Arr411.Core.Http;

/// <summary>Recognises an image from its first bytes (the cover cache only keeps bytes, not the original Content-Type).</summary>
internal static class ImageSniffer
{
    // Raw byte arrays: a "\x89"u8 literal would be encoded as two UTF-8 bytes, not as the byte 0x89.
    private static ReadOnlySpan<byte> Png => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static ReadOnlySpan<byte> Jpeg => [0xFF, 0xD8, 0xFF];

    private static ReadOnlySpan<byte> Icon => [0x00, 0x00, 0x01, 0x00];

    public static string? Detect(ReadOnlySpan<byte> data)
    {
        if (data.StartsWith(Png)) return "image/png";
        if (data.StartsWith(Jpeg)) return "image/jpeg";
        if (data.StartsWith("GIF87a"u8) || data.StartsWith("GIF89a"u8)) return "image/gif";
        if (data.Length >= 12 && data[..4].SequenceEqual("RIFF"u8) && data.Slice(8, 4).SequenceEqual("WEBP"u8)) return "image/webp";
        if (data.Length >= 12 && data.Slice(4, 4).SequenceEqual("ftyp"u8) && (data.Slice(8, 4).SequenceEqual("avif"u8) || data.Slice(8, 4).SequenceEqual("avis"u8))) return "image/avif";
        if (data.StartsWith("BM"u8)) return "image/bmp";
        if (data.StartsWith(Icon)) return "image/x-icon";
        return LooksLikeSvg(data) ? "image/svg+xml" : null;
    }

    private static bool LooksLikeSvg(ReadOnlySpan<byte> data)
    {
        var head = Encoding.UTF8.GetString(data[..Math.Min(data.Length, 1024)]).TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
        return head.StartsWith('<') && head.Contains("<svg", StringComparison.OrdinalIgnoreCase);
    }
}
