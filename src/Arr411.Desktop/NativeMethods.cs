using System.Runtime.InteropServices;

namespace Arr411.Desktop;

/// <summary>The few calls to Windows that the framework does not offer.</summary>
internal static class NativeMethods
{
    private const int ShcneAssocChanged = 0x08000000;
    private const uint ShcnfIdList = 0;
    private const int AsfwAny = -1;
    private const int SwRestore = 9;
    private const int DwmUseImmersiveDarkModeBefore20H1 = 19;
    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmBorderColor = 34;
    private const int DwmCaptionColor = 35;
    private const int DwmTextColor = 36;

    /// <summary>Tells Explorer that what opens a file type or a protocol changed, so that its lists are up to date.</summary>
    public static void NotifyAssociationsChanged() => SHChangeNotify(ShcneAssocChanged, ShcnfIdList, IntPtr.Zero, IntPtr.Zero);

    /// <summary>
    /// Lets the process that receives what this one hands over come to the front: Windows keeps a program that was not started by the
    /// user from taking the focus, and the running instance is not the one the user just started.
    /// </summary>
    public static void AllowAnyProcessToForeground() => _ = AllowSetForegroundWindow(AsfwAny);

    /// <summary>Brings a minimised window back as it was before (maximised or not).</summary>
    public static void Restore(IntPtr window) => _ = ShowWindow(window, SwRestore);

    /// <summary>
    /// Gives the title bar of a window the colours of the app instead of those of the system theme: a light bar above a dark app looks stuck on.
    /// Windows 11 takes the three colours; Windows 10 can only be told to go dark, and the versions before it ignore the call. Nothing changes in
    /// high contrast mode, whose colours are the user's to choose.
    /// </summary>
    public static void ColorTitleBar(IntPtr window, Color caption, Color text, Color border)
    {
        if (SystemInformation.HighContrast) return;

        var dark = 1;
        if (DwmSetWindowAttribute(window, DwmUseImmersiveDarkMode, ref dark, sizeof(int)) != 0)
        {
            // The number of this setting was another one before Windows 10 20H1.
            _ = DwmSetWindowAttribute(window, DwmUseImmersiveDarkModeBefore20H1, ref dark, sizeof(int));
        }

        SetTitleBarColor(window, DwmCaptionColor, caption);
        SetTitleBarColor(window, DwmTextColor, text);
        SetTitleBarColor(window, DwmBorderColor, border);
    }

    private static void SetTitleBarColor(IntPtr window, int attribute, Color color)
    {
        var colorRef = ColorTranslator.ToWin32(color);
        _ = DwmSetWindowAttribute(window, attribute, ref colorRef, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    [DllImport("shell32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(int processId);

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr window, int command);
}
