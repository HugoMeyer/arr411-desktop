using Arr411.Core.Logging;
using Arr411.Core.Shell;

namespace Arr411.Core.Bridge;

/// <summary>
/// How much one message may weigh, checked by the bridges before it is parsed: a page can post as much text as it has memory for,
/// and parsing that would freeze the window.
/// </summary>
internal static class MessageLimit
{
    /// <summary>
    /// The most characters of one message. The heaviest thing a page legitimately sends is a drop of files
    /// (<see cref="DroppedContent.MaxTotalBytes"/>, as base64: 4 characters for 3 bytes); the extra megabyte is for the text and the names around them.
    /// </summary>
    public const int MaxChars = (int)((DroppedContent.MaxTotalBytes + 2) / 3 * 4) + (1024 * 1024);

    /// <summary>False when the message is too long to be read: it is logged (without its content) and must be dropped.</summary>
    public static bool Accepts(string json, string source)
    {
        if (json.Length <= MaxChars) return true;

        Log.Warn($"{source} message refused: {json.Length} characters");
        return false;
    }
}
