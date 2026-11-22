using System.Runtime.InteropServices;
using Arr411.Core.Logging;

namespace Arr411.Desktop;

/// <summary>The clipboard of Windows, for text.</summary>
internal static class TextClipboard
{
    /// <summary>False when nothing was copied (another program holds the clipboard open): whoever asked for the copy says so instead of looking done.</summary>
    public static bool Copy(string text)
    {
#if DEBUG
        // Debug builds only, for the end-to-end tests, which must not overwrite what the user has copied: a file instead of the clipboard.
        if (Environment.GetEnvironmentVariable("ARR411_CLIPBOARD_SINK") is { Length: > 0 } sink)
        {
            try
            {
                File.WriteAllText(sink, text);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn("Clipboard sink not written: " + ex.Message);
                return false;
            }
        }
#endif
        try
        {
            Clipboard.SetDataObject(text, copy: true, retryTimes: 5, retryDelay: 100);
            return true;
        }
        catch (ExternalException ex)
        {
            Log.Warn("Clipboard unavailable: " + ex.Message);
            return false;
        }
    }
}
