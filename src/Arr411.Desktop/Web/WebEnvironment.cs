using Arr411.Core.Config;
using Microsoft.Web.WebView2.Core;

namespace Arr411.Desktop.Web;

/// <summary>The browser engine shared by every view of the app: one user data folder, one set of options.</summary>
internal static class WebEnvironment
{
    public const string RuntimeDownloadUrl = "https://developer.microsoft.com/microsoft-edge/webview2/";

    /// <summary>Debug builds only: the port of the DevTools protocol, used by the end-to-end tests.</summary>
    public const string DebugPortVariable = "ARR411_CDP_PORT";

    /// <summary>Debug builds only: the port of a local HTTPS server that stands in for c411.org and its subdomains (the fake site of the end-to-end tests).</summary>
    public const string TestSitePortVariable = "ARR411_TEST_SITE_PORT";

    // The hidden engine page has to keep its timers while the window is minimised or covered by another one.
    private const string KeepAliveArguments =
        "--disable-background-timer-throttling --disable-renderer-backgrounding --disable-backgrounding-occluded-windows "
        + "--disable-features=CalculateNativeWinOcclusion,IntensiveWakeUpThrottling";

    public static bool IsRuntimeInstalled()
    {
        try
        {
            return !string.IsNullOrEmpty(CoreWebView2Environment.GetAvailableBrowserVersionString());
        }
        catch (WebView2RuntimeNotFoundException)
        {
            return false;
        }
    }

    public static Task<CoreWebView2Environment> CreateAsync(AppPaths paths)
    {
        Directory.CreateDirectory(paths.WebViewData);

        // A magnet link is for this app, whatever program Windows has for it (often none). As a scheme the browser knows, a click on one
        // reaches the C411 view as a navigation it can hand to the web app, instead of the browser looking for a program to start.
        var schemes = new List<CoreWebView2CustomSchemeRegistration> { new("magnet") { HasAuthorityComponent = false } };
        var options = new CoreWebView2EnvironmentOptions(additionalBrowserArguments: Arguments(), customSchemeRegistrations: schemes);
        return CoreWebView2Environment.CreateAsync(null, paths.WebViewData, options);
    }

    private static string Arguments()
    {
        var arguments = KeepAliveArguments;
#if DEBUG
        if (int.TryParse(Environment.GetEnvironmentVariable(DebugPortVariable), out var port) && port is > 0 and < 65536)
        {
            arguments += " --remote-debugging-port=" + port;
        }

        if (int.TryParse(Environment.GetEnvironmentVariable(TestSitePortVariable), out var sitePort) && sitePort is > 0 and < 65536)
        {
            // The fake site has a certificate of its own making.
            arguments += $" --host-resolver-rules=\"MAP c411.org 127.0.0.1:{sitePort}, MAP *.c411.org 127.0.0.1:{sitePort}\" --ignore-certificate-errors";
        }
#endif
        return arguments;
    }
}
