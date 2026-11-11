using System.Reflection;
using System.Runtime.InteropServices;
using Arr411.Core.Logging;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Arr411.Desktop.Web;

/// <summary>
/// Tells a web view whether anyone can see it: a page nobody sees stops drawing and the browser may put it to sleep. The WinForms control
/// does it when it is hidden itself, but hears nothing when a container above it is hidden (another tab, the notification area, the task
/// bar), and it keeps the controller of the browser to itself. This reaches the property the way the control does.
/// </summary>
internal static class WebViewVisibility
{
    private static readonly FieldInfo? ControllerField = typeof(WebView2).GetField("_coreWebView2Controller", BindingFlags.Instance | BindingFlags.NonPublic);

    static WebViewVisibility()
    {
        if (ControllerField is null || ControllerField.FieldType != typeof(CoreWebView2Controller))
        {
            Log.Warn("The web views cannot be told when nobody can see them");
        }
    }

    /// <summary>Does nothing until the browser of the view is there: the view applies its state itself once it is ready.</summary>
    public static void Apply(WebView2 view, bool visible)
    {
        if (ControllerField?.GetValue(view) is not CoreWebView2Controller controller) return;

        try
        {
            controller.IsVisible = visible;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or COMException)
        {
            // The browser is going away.
        }
    }
}
