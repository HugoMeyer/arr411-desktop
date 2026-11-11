namespace Arr411.Desktop.Web;

/// <summary>A thin bar at the top of the C411 tab that sweeps while a page loads. It always takes its height, so the page does not jump.</summary>
internal sealed class LoadingBar : Control
{
    private const int SegmentWidth = 160;
    private const int Step = 14;

    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 20 };
    private int _position;

    public LoadingBar()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        TabStop = false;
        _timer.Tick += (_, _) =>
        {
            _position = (_position + Step) % (Width + SegmentWidth);
            Invalidate();
        };
    }

    protected override Size DefaultSize => new(100, 3);

    public void Start()
    {
        _position = 0;
        _timer.Start();
        Invalidate();
    }

    public void Stop()
    {
        _timer.Stop();
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Theme.Background);
        if (!_timer.Enabled) return;

        using var brush = new SolidBrush(Theme.Accent);
        e.Graphics.FillRectangle(brush, _position - SegmentWidth, 0, SegmentWidth, Height);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _timer.Dispose();
        base.Dispose(disposing);
    }
}
