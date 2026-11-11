using System.Drawing;

namespace Arr411.Core.Shell;

/// <summary>Where the window was, in screen pixels (the restored position when it was maximized).</summary>
public readonly record struct WindowBounds(int X, int Y, int Width, int Height, bool Maximized);

/// <summary>Decides whether the position the window had last time is still usable (a screen may have been unplugged since).</summary>
public static class WindowPlacement
{
    /// <summary>How much of the title bar must be on a screen for the user to be able to grab the window.</summary>
    private const int GrabWidth = 120;

    private const int GrabHeight = 32;

    /// <summary>
    /// The bounds to open the window with, or null when the saved ones would put it out of reach (then the window is centred).
    /// The size is kept within the minimum size of the window and the screen it lands on.
    /// </summary>
    /// <param name="saved">What was saved.</param>
    /// <param name="workAreas">The usable area of every screen (without the taskbar).</param>
    /// <param name="minimum">The smallest size the window accepts.</param>
    public static WindowBounds? Fit(WindowBounds saved, IReadOnlyList<Rectangle> workAreas, Size minimum)
    {
        if (workAreas.Count == 0 || saved.Width <= 0 || saved.Height <= 0) return null;

        var bounds = new Rectangle(saved.X, saved.Y, saved.Width, saved.Height);
        var best = Rectangle.Empty;
        var bestArea = 0L;
        foreach (var area in workAreas)
        {
            var overlap = Rectangle.Intersect(area, bounds);
            var size = (long)overlap.Width * overlap.Height;
            if (size > bestArea)
            {
                best = area;
                bestArea = size;
            }
        }

        if (bestArea == 0) return null;

        // The title bar is what the user grabs: enough of it has to be on the screen.
        var title = Rectangle.Intersect(best, new Rectangle(bounds.X, bounds.Y, bounds.Width, GrabHeight));
        if (title.Width < Math.Min(GrabWidth, bounds.Width) || title.Height < GrabHeight) return null;

        var width = Math.Clamp(bounds.Width, Math.Min(minimum.Width, best.Width), best.Width);
        var height = Math.Clamp(bounds.Height, Math.Min(minimum.Height, best.Height), best.Height);
        return new WindowBounds(bounds.X, bounds.Y, width, height, saved.Maximized);
    }

    /// <summary>
    /// The client size to open a window that has no place to come back to, so that it does not hang out of the screen: the usual size is made
    /// for a big screen, and a laptop shows less (a 1080p one at 150 % has no room for 720 dp of height).
    /// </summary>
    /// <param name="wanted">The usual size of the client area, in pixels.</param>
    /// <param name="frame">What the borders and the title bar add around the client area.</param>
    /// <param name="workArea">The usable area of the screen the window opens on.</param>
    public static Size FitClient(Size wanted, Size frame, Rectangle workArea) =>
        new(Math.Max(1, Math.Min(wanted.Width, workArea.Width - frame.Width)), Math.Max(1, Math.Min(wanted.Height, workArea.Height - frame.Height)));
}
