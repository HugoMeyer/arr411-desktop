using System.Drawing;
using System.Windows.Forms;

namespace Arr411.E2E;

/// <summary>
/// Drags something over the window of the app with the real mouse and lets go, as Explorer or a browser does when the user drops a file or a
/// link on it: a small window of the test starts the drag, and the app is asked about it through the same system calls as in real life.
/// The real cursor moves for about a second and is put back. One drag at a time.
/// </summary>
internal static class DragSource
{
    private const int Size = 60;
    private const int Margin = 10;
    private const int Steps = 12;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExToolWindow = 0x00000080;

    /// <summary>Drags the data from a corner of the screen to a point of the client area of the window, and drops it there.</summary>
    /// <returns>What the app answered: Copy when it took the drop, None when it refused it.</returns>
    public static async Task<DragDropEffects> DropAsync(IntPtr window, int x, int y, Func<DataObject> data)
    {
        if (!Win32.HasKeyboard(window) && !Win32.Foreground(window)) throw new InvalidOperationException("Windows would not put the app in front, so nothing was dragged.");

        var (targetX, targetY) = Win32.ToScreen(window, x, y);
        if (!Win32.IsOwnWindowAt(window, targetX, targetY)) throw new InvalidOperationException($"Another program is under the point {targetX},{targetY}: nothing was dragged.");

        var answer = new TaskCompletionSource<DragDropEffects>(TaskCreationOptions.RunContinuationsAsynchronously);
        var shown = new TaskCompletionSource<Rectangle>(TaskCreationOptions.RunContinuationsAsynchronously);
        var spot = Spot(window);
        Form? source = null;
        var thread = new Thread(() =>
        {
            using var form = new SourceForm(spot);
            source = form;
            form.MouseDown += (_, _) =>
            {
                try
                {
                    answer.TrySetResult(form.DoDragDrop(data(), DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link));
                }
                catch (Exception ex)
                {
                    answer.TrySetException(ex);
                }
            };
            form.Shown += (_, _) => shown.TrySetResult(form.Bounds);
            Application.Run(form);
        })
        { IsBackground = true, Name = "drag source" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        var before = Win32.CursorPosition();
        var pressed = false;
        try
        {
            var bounds = await shown.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var (fromX, fromY) = (bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);

            Win32.MoveCursor(fromX, fromY);
            await Task.Delay(150);
            Win32.PressLeftButton();
            pressed = true;
            await Task.Delay(150);
            for (var step = 1; step <= Steps; step++)
            {
                Win32.MoveCursor(fromX + (targetX - fromX) * step / Steps, fromY + (targetY - fromY) * step / Steps);
                await Task.Delay(30);
            }

            // Time for the app to answer the drag over the window (what the cursor would show).
            await Task.Delay(400);
            Win32.ReleaseLeftButton();
            pressed = false;
            return await answer.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            // A button that stays down would keep acting on the machine of whoever is using it.
            if (pressed) Win32.ReleaseLeftButton();
            Win32.MoveCursor(before.X, before.Y);
            if (source is { IsHandleCreated: true } form)
            {
                try
                {
                    form.BeginInvoke(form.Close);
                }
                catch (InvalidOperationException)
                {
                    // Already gone.
                }
            }

            thread.Join(TimeSpan.FromSeconds(5));
        }
    }

    // A corner of the screen the window of the app does not cover, so that nothing but the cursor ever passes over the window.
    private static Rectangle Spot(IntPtr window)
    {
        var area = Screen.PrimaryScreen!.WorkingArea;
        var (left, top, width, height) = Win32.Bounds(window);
        var frame = new Rectangle(left, top, width, height);
        Rectangle[] corners =
        [
            new(area.Left + Margin, area.Top + Margin, Size, Size),
            new(area.Right - Margin - Size, area.Top + Margin, Size, Size),
            new(area.Left + Margin, area.Bottom - Margin - Size, Size, Size),
            new(area.Right - Margin - Size, area.Bottom - Margin - Size, Size, Size),
        ];
        return corners.FirstOrDefault(corner => !corner.IntersectsWith(frame), corners[0]);
    }

    // On top of everything, but never in front of the app for the keyboard: the app keeps the focus all along.
    private sealed class SourceForm : Form
    {
        public SourceForm(Rectangle spot)
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Bounds = spot;
            ShowInTaskbar = false;
            TopMost = true;
            BackColor = Color.OrangeRed;
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var parameters = base.CreateParams;
                parameters.ExStyle |= WsExNoActivate | WsExToolWindow;
                return parameters;
            }
        }
    }
}
