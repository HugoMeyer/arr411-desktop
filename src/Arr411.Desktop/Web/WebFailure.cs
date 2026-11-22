using System.Text;
using Arr411.Core.Diagnostics;
using Microsoft.Web.WebView2.Core;

namespace Arr411.Desktop.Web;

/// <summary>What the log says when a process of the browser behind the web views stops.</summary>
internal static class WebFailure
{
    private const long UnresponsiveLineGapMilliseconds = 60_000;

    public static string Describe(CoreWebView2ProcessFailedEventArgs e)
    {
        var text = new StringBuilder();
        try
        {
            text.Append(WebFailureText.Describe(e.ProcessFailedKind.ToString(), e.Reason.ToString(), e.ExitCode, Safe(() => e.ProcessDescription), Safe(() => e.FailureSourceModulePath)));
        }
        catch (Exception ex)
        {
            // An old runtime does not tell everything: the kind is always known.
            text.Append(e.ProcessFailedKind).Append(" (details unavailable: ").Append(ex.GetType().Name).Append(')');
        }

        // A page that grew too big shows in the memory of the browser processes, which this one cannot see by itself.
        try
        {
            text.Append("; ").Append(MemorySnapshot.Take()).Append("; ").Append(ChildProcesses.Take());
        }
        catch (Exception ex)
        {
            text.Append("; memory unknown (").Append(ex.GetType().Name).Append(')');
        }

        return text.ToString();
    }

    private static string? Safe(Func<string?> read)
    {
        try
        {
            return read();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether a failure is worth a line of the log. A page that does not answer is reported again every few seconds for as long as it
    /// stays so: one line a minute is enough. <paramref name="lastLine"/> is when the last one was written (0 before the first).
    /// </summary>
    public static bool IsWorthALine(CoreWebView2ProcessFailedKind kind, ref long lastLine)
    {
        if (kind != CoreWebView2ProcessFailedKind.RenderProcessUnresponsive) return true;

        var now = Environment.TickCount64;
        if (lastLine != 0 && now - lastLine < UnresponsiveLineGapMilliseconds) return false;

        lastLine = now;
        return true;
    }
}
