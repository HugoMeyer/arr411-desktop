using System.ComponentModel;
using System.Diagnostics;
using Arr411.Core.Logging;

namespace Arr411.Desktop;

/// <summary>Opens an address in the program Windows chooses for it: a web page in the default browser, or the page of Windows Settings (ms-settings:). Callers only pass http(s) links (see ExternalLinks) or addresses of their own.</summary>
internal static class ExternalBrowser
{
    /// <summary>Debug builds only: a file the addresses are written to (one per line) instead of starting a browser. For the end-to-end tests.</summary>
    public const string SinkVariable = "ARR411_BROWSER_SINK";

    public static void Open(string url)
    {
#if DEBUG
        if (Environment.GetEnvironmentVariable(SinkVariable) is { Length: > 0 } sink)
        {
            try
            {
                File.AppendAllText(sink, url + "\n");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn("Browser sink not written: " + ex.Message);
            }

            return;
        }
#endif
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            Log.Warn("Browser not started: " + ex.Message);
        }
    }
}
