using System.Runtime.InteropServices;
using System.Text;

namespace Arr411.E2E;

/// <summary>
/// What the tests do to the windows of the app from the outside: find them, click them, type in them. Clicks are posted to the window
/// under the point, so that the mouse of whoever is using the machine stays where it is; the few things that only a real click shows
/// (what happens to a menu when the page is clicked) move the real cursor for a moment. Keys can only be sent to the window that has the keyboard.
/// </summary>
public static class Win32
{
    public const uint Close = 0x0010;

    // The virtual keys the tests press.
    public const ushort Control = 0x11;
    public const ushort Shift = 0x10;
    public const ushort Alt = 0x12;
    public const ushort Left = 0x25;
    public const ushort Down = 0x28;
    public const ushort Home = 0x24;
    public const ushort Enter = 0x0D;
    public const ushort Escape = 0x1B;
    public const ushort F5 = 0x74;
    public const ushort Tab = 0x09;
    public const ushort PageUp = 0x21;
    public const ushort PageDown = 0x22;
    public const ushort Comma = 0xBC;
    public const ushort BrowserBack = 0xA6;
    public const ushort R = 0x52;

    /// <summary>The key of a digit of the main row.</summary>
    public static ushort Digit(int digit) => (ushort)('0' + digit);

    private const uint LeftDown = 0x0201;
    private const uint LeftUp = 0x0202;
    private const uint RightDown = 0x0204;
    private const uint RightUp = 0x0205;
    private const uint MouseEventLeftDown = 0x0002;
    private const uint MouseEventLeftUp = 0x0004;
    private const uint SkipInvisible = 0x1;
    private const uint GwOwner = 4;
    private const uint GaRoot = 2;
    private const uint SysCommand = 0x0112;
    private const uint ScClose = 0xF060;
    private const uint InputKeyboard = 1;
    private const uint KeyUp = 0x2;
    private const uint ExtendedKey = 0x1;
    private const int Restore = 9;
    private const int Minimized = 6;
    private const int Maximized = 3;
    private const double BaseDpi = 96.0;
    private static readonly IntPtr PerMonitorAwareV2 = new(-4);

    // The geometry of the navigation bar, in dp (see Nav\NavBar.cs).
    private const double BarItemHeight = 64;
    private const double BarEdgeMargin = 8;
    private const double BarWidth = 76;

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int X;
        public int Y;
        public uint Data;
        public uint Flags;
        public uint Time;
        public UIntPtr Extra;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort Key;
        public ushort Scan;
        public uint Flags;
        public uint Time;
        public UIntPtr Extra;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public MouseInput Mouse;

        [FieldOffset(0)]
        public KeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion Data;
    }

    static Win32()
    {
        // Without this, the coordinates of a DPI-aware app are reported scaled down.
        SetProcessDpiAwarenessContext(PerMonitorAwareV2);
    }

    /// <summary>The window of the app, whether or not it is on screen (it is hidden while the app lives in the notification area). Zero if there is none.</summary>
    public static IntPtr MainWindow(int processId)
    {
        var found = IntPtr.Zero;
        EnumWindows(
            (window, _) =>
            {
                GetWindowThreadProcessId(window, out var owner);
                if (owner != (uint)processId || GetWindow(window, GwOwner) != IntPtr.Zero || Title(window) != "Arr411") return true;

                found = window;
                return false;
            },
            IntPtr.Zero);
        return found;
    }

    /// <summary>How many windows of the process are on screen: the main one, and a menu or a dialog when one is open (the shadow Windows puts under a menu is not one).</summary>
    public static int VisibleWindowCount(int processId)
    {
        var count = 0;
        EnumWindows(
            (window, _) =>
            {
                GetWindowThreadProcessId(window, out var owner);
                if (owner == (uint)processId && IsWindowVisible(window) && ClassName(window) != "SysShadow") count++;
                return true;
            },
            IntPtr.Zero);
        return count;
    }

    /// <summary>The windows of the process that are on screen, one per line: what a test says when it found another number than the one it expected.</summary>
    public static string DescribeVisibleWindows(int processId)
    {
        var lines = new List<string>();
        EnumWindows(
            (window, _) =>
            {
                GetWindowThreadProcessId(window, out var owner);
                if (owner != (uint)processId || !IsWindowVisible(window)) return true;

                GetWindowRect(window, out var rect);
                lines.Add($"{window}: class '{ClassName(window)}', title '{Title(window)}', {rect.Left},{rect.Top} {rect.Right - rect.Left}x{rect.Bottom - rect.Top}");
                return true;
            },
            IntPtr.Zero);
        return string.Join('\n', lines);
    }

    public static bool IsVisible(IntPtr window) => window != IntPtr.Zero && IsWindowVisible(window);

    public static bool IsMinimized(IntPtr window) => IsIconic(window);

    public static bool IsMaximized(IntPtr window) => IsZoomed(window);

    /// <summary>Minimizes the window, as its minimize button does.</summary>
    public static void Minimize(IntPtr window) => ShowWindow(window, Minimized);

    /// <summary>Maximizes the window, as its maximize button does.</summary>
    public static void Maximize(IntPtr window) => ShowWindow(window, Maximized);

    /// <summary>Brings a maximized (or minimized) window back to its normal place, as the restore button does.</summary>
    public static void Unmaximize(IntPtr window) => ShowWindow(window, Restore);

    /// <summary>The usable area of the primary screen (what the taskbar leaves), in pixels.</summary>
    public static System.Drawing.Rectangle PrimaryWorkArea() => System.Windows.Forms.Screen.PrimaryScreen!.WorkingArea;

    /// <summary>Puts the window at these bounds (its frame included), in pixels, as a user does by dragging it.</summary>
    public static void Move(IntPtr window, int left, int top, int width, int height)
    {
        if (!MoveWindow(window, left, top, width, height, true)) throw new InvalidOperationException("The window could not be moved.");
    }

    /// <summary>The bounds of the window on screen (its frame included), in pixels.</summary>
    public static (int Left, int Top, int Width, int Height) Bounds(IntPtr window)
    {
        GetWindowRect(window, out var rect);
        return (rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
    }

    /// <summary>The size of the area inside the frame of the window, in pixels.</summary>
    public static (int Width, int Height) ClientSize(IntPtr window)
    {
        GetClientRect(window, out var rect);
        return (rect.Right - rect.Left, rect.Bottom - rect.Top);
    }

    /// <summary>The message box of the process, if one is on screen (a window of the class of the standard dialogs). Zero if there is none.</summary>
    public static IntPtr Dialog(int processId)
    {
        var found = IntPtr.Zero;
        EnumWindows(
            (window, _) =>
            {
                GetWindowThreadProcessId(window, out var owner);
                if (owner != (uint)processId || !IsWindowVisible(window) || ClassName(window) != "#32770") return true;

                found = window;
                return false;
            },
            IntPtr.Zero);
        return found;
    }

    public static void Post(IntPtr window, uint message) => PostMessage(window, message, UIntPtr.Zero, IntPtr.Zero);

    /// <summary>What the close button of the window (or Alt+F4) does. A bare WM_CLOSE is what the system sends, which is not the same for the app.</summary>
    public static void PressClose(IntPtr window) => PostMessage(window, SysCommand, (UIntPtr)ScClose, IntPtr.Zero);

    public static double Scale(IntPtr window) => GetDpiForWindow(window) / BaseDpi;

    /// <summary>
    /// What a window of another process that is in front of the window is (title, class, program, bounds), at some points of its client
    /// area; null if the window is in front (a picture of the screen would show it).
    /// </summary>
    public static string? CoveredBy(IntPtr window)
    {
        GetClientRect(window, out var client);
        GetWindowThreadProcessId(window, out var owner);
        var width = client.Right - client.Left;
        var height = client.Bottom - client.Top;
        foreach (var (x, y) in new[] { (width / 2, height / 2), (20, 20), (width - 20, 20), (20, height - 20), (width - 20, height - 20) })
        {
            var point = new NativePoint { X = x, Y = y };
            ClientToScreen(window, ref point);
            var found = WindowFromPoint(point);
            if (found == IntPtr.Zero) continue;

            var root = GetAncestor(found, GaRoot);
            GetWindowThreadProcessId(root, out var other);
            if (root != window && other != owner) return Describe(root, other);
        }

        return null;
    }

    private static string Describe(IntPtr window, uint processId)
    {
        string program;
        try
        {
            program = System.Diagnostics.Process.GetProcessById((int)processId).ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            program = "unknown";
        }

        GetWindowRect(window, out var rect);
        var title = Title(window);
        return $"'{(title.Length > 0 ? title : "no title")}', class {ClassName(window)}, program {program}, {rect.Left},{rect.Top} {rect.Right - rect.Left}x{rect.Bottom - rect.Top}";
    }

    /// <summary>The centre of a tab of the navigation bar, in pixels from the top left corner of the client area (0 is the first tab).</summary>
    public static (int X, int Y) TabCenter(IntPtr window, int index)
    {
        var scale = Scale(window);
        return ((int)Math.Round(BarWidth / 2 * scale), (int)Math.Round((BarEdgeMargin + BarItemHeight * index + BarItemHeight / 2) * scale));
    }

    /// <summary>The centre of the menu button at the bottom of the navigation bar.</summary>
    public static (int X, int Y) MenuCenter(IntPtr window)
    {
        var scale = Scale(window);
        GetClientRect(window, out var client);
        return ((int)Math.Round(BarWidth / 2 * scale), client.Bottom - (int)Math.Round((BarEdgeMargin + BarItemHeight / 2) * scale));
    }

    /// <summary>
    /// Clicks at a point of the client area of the window: the mouse messages go to the innermost window under that point, as the
    /// system would send them, without moving the real mouse. Returns the window that was clicked.
    /// </summary>
    public static IntPtr Click(IntPtr window, int x, int y, bool right = false)
    {
        var target = window;
        var point = new NativePoint { X = x, Y = y };
        while (true)
        {
            var child = ChildWindowFromPointEx(target, point, SkipInvisible);
            if (child == IntPtr.Zero || child == target) break;

            ClientToScreen(target, ref point);
            ScreenToClient(child, ref point);
            target = child;
        }

        var position = (IntPtr)((point.Y << 16) | (point.X & 0xFFFF));
        PostMessage(target, right ? RightDown : LeftDown, (UIntPtr)(right ? 2 : 1), position);
        PostMessage(target, right ? RightUp : LeftUp, UIntPtr.Zero, position);
        return target;
    }

    /// <summary>
    /// Clicks with the real mouse, as a hand does: the system sends the click wherever the point is, whichever program that is. It is the only
    /// way to see what a click does in a window of another process (the web page) and how a menu goes away when it is clicked away. The
    /// real cursor moves for a moment and is put back. Nothing is clicked when something else than the app is under the point.
    /// </summary>
    public static async Task RealClickAsync(IntPtr window, int x, int y)
    {
        GetWindowThreadProcessId(window, out var owner);
        var point = new NativePoint { X = x, Y = y };
        ClientToScreen(window, ref point);

        var root = GetAncestor(WindowFromPoint(point), GaRoot);
        GetWindowThreadProcessId(root, out var other);
        if (root == IntPtr.Zero || other != owner)
        {
            throw new InvalidOperationException($"Another program is under the point {point.X},{point.Y} ('{Title(root)}'): the click was not made.");
        }

        GetCursorPos(out var before);
        try
        {
            SetCursorPos(point.X, point.Y);
            await Task.Delay(100);
            mouse_event(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
            mouse_event(MouseEventLeftUp, 0, 0, 0, UIntPtr.Zero);
            await Task.Delay(300);
        }
        finally
        {
            SetCursorPos(before.X, before.Y);
        }
    }

    /// <summary>The point of the screen that is at a point of the client area of the window.</summary>
    public static (int X, int Y) ToScreen(IntPtr window, int x, int y)
    {
        var point = new NativePoint { X = x, Y = y };
        ClientToScreen(window, ref point);
        return (point.X, point.Y);
    }

    /// <summary>Whether the window that is on screen at this point belongs to the same program as the window (and not to another one on top of it).</summary>
    public static bool IsOwnWindowAt(IntPtr window, int screenX, int screenY)
    {
        GetWindowThreadProcessId(window, out var owner);
        var root = GetAncestor(WindowFromPoint(new NativePoint { X = screenX, Y = screenY }), GaRoot);
        GetWindowThreadProcessId(root, out var other);
        return root != IntPtr.Zero && other == owner;
    }

    public static (int X, int Y) CursorPosition()
    {
        GetCursorPos(out var point);
        return (point.X, point.Y);
    }

    /// <summary>A point of the title bar of the window, away from its buttons: a cursor that rests there makes the system show nothing.</summary>
    public static (int X, int Y) CaptionCenter(IntPtr window)
    {
        GetWindowRect(window, out var frame);
        var client = new NativePoint();
        ClientToScreen(window, ref client);
        return ((frame.Left + frame.Right) / 2, (frame.Top + client.Y) / 2);
    }

    /// <summary>Moves the real cursor. Only for the tests that need the real mouse (see <see cref="DragSource"/>): they put it back afterwards.</summary>
    public static void MoveCursor(int x, int y) => SetCursorPos(x, y);

    public static void PressLeftButton() => mouse_event(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);

    public static void ReleaseLeftButton() => mouse_event(MouseEventLeftUp, 0, 0, 0, UIntPtr.Zero);

    /// <summary>Puts the window in front and gives it the keyboard. False if Windows would not let it.</summary>
    public static bool Foreground(IntPtr window)
    {
        if (IsIconic(window)) ShowWindow(window, Restore);

        var current = GetCurrentThreadId();
        var other = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        var attached = other != 0 && other != current && AttachThreadInput(current, other, true);
        try
        {
            BringWindowToTop(window);
            SetForegroundWindow(window);
        }
        finally
        {
            if (attached) AttachThreadInput(current, other, false);
        }

        return GetForegroundWindow() == window;
    }

    public static bool HasKeyboard(IntPtr window) => GetForegroundWindow() == window;

    /// <summary>The text of the controls the window shows on screen: what a message of the app (its toast) says.</summary>
    public static IReadOnlyList<string> VisibleTexts(IntPtr window)
    {
        var texts = new List<string>();
        EnumChildWindows(
            window,
            (child, _) =>
            {
                if (IsWindowVisible(child) && Title(child) is { Length: > 0 } text) texts.Add(text);
                return true;
            },
            IntPtr.Zero);
        return texts;
    }

    /// <summary>Presses the keys in order, then lets them go in the reverse order: "Ctrl+Shift+Tab" is Chord(Control, Shift, Tab).</summary>
    public static void Chord(params ushort[] keys)
    {
        var inputs = new List<Input>();
        foreach (var key in keys) inputs.Add(KeyEvent(key, up: false));
        foreach (var key in keys.Reverse()) inputs.Add(KeyEvent(key, up: true));

        var sent = SendInput((uint)inputs.Count, [.. inputs], Marshal.SizeOf<Input>());
        if (sent != inputs.Count)
        {
            // A key that stays down would keep acting on the machine of whoever is using it.
            ReleaseAll(keys);
            throw new InvalidOperationException("Windows did not accept the keys.");
        }
    }

    private static void ReleaseAll(ushort[] keys)
    {
        var inputs = keys.Select(key => KeyEvent(key, up: true)).ToArray();
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
    }

    private static Input KeyEvent(ushort key, bool up)
    {
        var extended = key is Left or Down or PageUp or PageDown or Home or 0x26 or 0x27;
        var flags = (up ? KeyUp : 0) | (extended ? ExtendedKey : 0);
        return new Input { Type = InputKeyboard, Data = new InputUnion { Keyboard = new KeyboardInput { Key = key, Flags = flags } } };
    }

    private static string Title(IntPtr window)
    {
        var text = new StringBuilder(256);
        return GetWindowText(window, text, text.Capacity) > 0 ? text.ToString() : "";
    }

    private static string ClassName(IntPtr window)
    {
        var text = new StringBuilder(256);
        return GetClassName(window, text, text.Capacity) > 0 ? text.ToString() : "";
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr window, StringBuilder text, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsZoomed(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveWindow(IntPtr window, int left, int top, int width, int height, [MarshalAs(UnmanagedType.Bool)] bool repaint);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr window, uint command);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr window, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(NativePoint point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr ChildWindowFromPointEx(IntPtr parent, NativePoint point, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(IntPtr window, ref NativePoint point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ScreenToClient(IntPtr window, ref NativePoint point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr window, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint attach, uint to, [MarshalAs(UnmanagedType.Bool)] bool attached);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    [DllImport("user32.dll")]
    private static extern uint SendInput(uint count, Input[] inputs, int size);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
