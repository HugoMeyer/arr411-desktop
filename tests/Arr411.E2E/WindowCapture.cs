using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Arr411.E2E;

/// <summary>
/// Takes a picture of what a window shows on the screen. PrintWindow cannot be used: it returns the window frame but not the web view
/// (which is composed separately), so a blank window would look like a rendered one.
/// </summary>
public static class WindowCapture
{
    private const uint NoSize = 0x1;
    private const uint NoMove = 0x2;
    private const uint NoActivate = 0x10;
    private const int Restore = 9;
    private const int ExtendedFrameBounds = 9;
    private const long CoverWaitMilliseconds = 8_000;
    private static readonly IntPtr PerMonitorAwareV2 = new(-4);
    private static readonly IntPtr TopMost = new(-1);
    private static readonly IntPtr NoTopMost = new(-2);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr window, out Rect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(IntPtr window, ref Point point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out Rect value, int size);

    /// <summary>
    /// The client area of the window (without its frame), as the screen shows it. The window is put in front for a moment. It has to be:
    /// whatever is in front of it would be in the picture, and a test would then judge the wrong windows. When something is in front anyway,
    /// the real cursor is moved away from it for a moment and put back.
    /// </summary>
    public static Task<Bitmap> CaptureAsync(IntPtr window) => TakeAsync(window, titleBar: false);

    /// <summary>The title bar of the window (the part of its visible frame above the client area), as the screen shows it. Taken like <see cref="CaptureAsync"/>.</summary>
    public static Task<Bitmap> CaptureTitleBarAsync(IntPtr window) => TakeAsync(window, titleBar: true);

    private static async Task<Bitmap> TakeAsync(IntPtr window, bool titleBar)
    {
        // Without this, the sizes of a DPI-aware app are reported scaled down and the picture is cropped.
        SetProcessDpiAwarenessContext(PerMonitorAwareV2);
        if (IsIconic(window)) ShowWindow(window, Restore);

        Win32.Foreground(window);
        SetWindowPos(window, TopMost, 0, 0, 0, 0, NoMove | NoSize | NoActivate);
        var cursor = Win32.CursorPosition();
        var parked = false;
        try
        {
            await Task.Delay(400);
            if (!GetClientRect(window, out var client)) throw new InvalidOperationException("The window is gone.");

            var cover = Win32.CoveredBy(window);
            if (cover is not null)
            {
                // The cursor of whoever uses the machine can rest on a button of the frame of the window: the system then shows a flyout under it
                // (the layouts of the windows, for the maximize button), in front of the page, until the cursor goes away.
                var (parkX, parkY) = Win32.CaptionCenter(window);
                Win32.MoveCursor(parkX, parkY);
                parked = true;
                cover = await UncoveredAsync(window);
            }

            if (cover is not null) throw new InvalidOperationException($"The window of the app is covered by another one ({cover}), so it cannot be photographed.");

            if (titleBar)
            {
                var (captionX, captionY) = Win32.CaptionCenter(window);
                if (!Win32.IsOwnWindowAt(window, captionX, captionY)) throw new InvalidOperationException("The title bar of the window is covered by another window, so it cannot be photographed.");
            }

            var origin = new Point();
            ClientToScreen(window, ref origin);
            var area = titleBar
                ? TitleBarArea(window, origin.Y)
                : new Rectangle(origin.X, origin.Y, client.Right - client.Left, client.Bottom - client.Top);
            var bitmap = new Bitmap(Math.Max(1, area.Width), Math.Max(1, area.Height), PixelFormat.Format32bppArgb);
            using var graphics = Graphics.FromImage(bitmap);
            graphics.CopyFromScreen(area.X, area.Y, 0, 0, bitmap.Size);
            return bitmap;
        }
        finally
        {
            SetWindowPos(window, NoTopMost, 0, 0, 0, 0, NoMove | NoSize | NoActivate);
            if (parked) Win32.MoveCursor(cursor.X, cursor.Y);
        }
    }

    // The visible frame, which is smaller than the window: Windows gives it invisible borders to grab when it is resized.
    private static Rectangle TitleBarArea(IntPtr window, int clientTop)
    {
        if (DwmGetWindowAttribute(window, ExtendedFrameBounds, out var frame, Marshal.SizeOf<Rect>()) != 0) throw new InvalidOperationException("The frame of the window could not be measured.");

        return new Rectangle(frame.Left, frame.Top, frame.Right - frame.Left, clientTop - frame.Top);
    }

    // A tooltip or a notification of the system can sit on top of the window for a few seconds, and it may come after the window was put in
    // front: wait for it to go away before saying that the window is covered. Null when nothing covers the window.
    private static async Task<string?> UncoveredAsync(IntPtr window)
    {
        var deadline = Environment.TickCount64 + CoverWaitMilliseconds;
        while (true)
        {
            var cover = Win32.CoveredBy(window);
            if (cover is null || Environment.TickCount64 >= deadline) return cover;

            await Task.Delay(250);
        }
    }

    /// <summary>How many different colours there are in the picture (a blank window has one, a rendered page has hundreds).</summary>
    public static int DistinctColors(Bitmap bitmap) => Pixels(bitmap).Distinct().Count();

    /// <summary>How many pixels are within <paramref name="tolerance"/> of a colour, on each channel.</summary>
    public static int CountNear(Bitmap bitmap, Color color, int tolerance = 8) =>
        Pixels(bitmap).Count(pixel => IsNear(pixel, color, tolerance));

    /// <summary>How many pixels of an area of the picture are within <paramref name="tolerance"/> of a colour, on each channel.</summary>
    public static int CountNear(Bitmap bitmap, Rectangle area, Color color, int tolerance = 8)
    {
        area.Intersect(new Rectangle(0, 0, bitmap.Width, bitmap.Height));
        if (area.Width <= 0 || area.Height <= 0) return 0;

        using var part = bitmap.Clone(area, PixelFormat.Format32bppArgb);
        return CountNear(part, color, tolerance);
    }

    /// <summary>
    /// How many separate vertical bands of the picture hold a colour. The posters of a row are of one colour and are told apart by the gaps
    /// between them, so a picture of them has as many bands as the row has posters.
    /// </summary>
    public static int CountBandsNear(Bitmap bitmap, Color color, int tolerance = 8)
    {
        var pixels = Pixels(bitmap);
        var bands = 0;
        var inBand = false;
        for (var x = 0; x < bitmap.Width; x++)
        {
            var found = false;
            for (var y = 0; y < bitmap.Height && !found; y++) found = IsNear(pixels[y * bitmap.Width + x], color, tolerance);

            if (found && !inBand) bands++;
            inBand = found;
        }

        return bands;
    }

    private static bool IsNear(int pixel, Color color, int tolerance) =>
        Math.Abs(((pixel >> 16) & 0xFF) - color.R) <= tolerance
        && Math.Abs(((pixel >> 8) & 0xFF) - color.G) <= tolerance
        && Math.Abs((pixel & 0xFF) - color.B) <= tolerance;

    private static int[] Pixels(Bitmap bitmap)
    {
        var area = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(area, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var pixels = new int[bitmap.Width * bitmap.Height];
            for (var row = 0; row < bitmap.Height; row++)
            {
                Marshal.Copy(data.Scan0 + row * data.Stride, pixels, row * bitmap.Width, bitmap.Width);
            }

            return pixels;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }
}
