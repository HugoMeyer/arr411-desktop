using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Globalization;
using Arr411.Core.Drawing;

namespace Arr411.Desktop.Nav;

/// <summary>
/// One entry of the navigation bar, drawn like the Android tab bar: the icon, in a pill when the tab is the current one, over its label, and
/// a counter on the qBittorrent tab. Everything is sized in device pixels from the DPI of the window it is in.
/// </summary>
internal sealed class NavItem : Control
{
    // Sizes of the Android bar, in dp.
    private const float PillWidth = 54f;
    private const float PillHeight = 30f;
    private const float IconSize = 24f;
    private const float TopMargin = 8f;
    private const float LabelGap = 3f;
    private const float BadgeHeight = 18f;
    private const float BadgeOffset = 32f;

    private readonly SvgPath _icon;
    private Font _labelFont = null!;
    private Font _selectedFont = null!;
    private Font _badgeFont = null!;
    private GraphicsPath? _iconPath;
    private int _metricsDpi;
    private bool _selected;
    private bool _hot;
    private bool _pressed;
    private int _badge;

    public NavItem(string tab, string caption, SvgPath icon)
    {
        Name = tab;
        Caption = caption;
        _icon = icon;
        SetStyle(
            ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable,
            true);
        TabStop = true;
        Cursor = Cursors.Hand;
        BackColor = Theme.Bar;
    }

    /// <summary>The label under the icon.</summary>
    public string Caption { get; }

    /// <summary>The click, Enter and Space: show the tab.</summary>
    public event EventHandler? Invoked;

    /// <summary>The right click, the menu key and Shift+F10 (a long press on a touch screen): the secondary action.</summary>
    public event EventHandler? Alternate;

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Selected
    {
        get => _selected;
        set
        {
            if (_selected == value) return;

            _selected = value;
            Invalidate();
            AccessibilityNotifyClients(AccessibleEvents.StateChange, -1);
        }
    }

    /// <summary>The counter shown over the icon; nothing when zero.</summary>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Badge
    {
        get => _badge;
        set
        {
            value = Math.Max(0, value);
            if (_badge == value) return;

            _badge = value;
            Invalidate();
            AccessibilityNotifyClients(AccessibleEvents.NameChange, -1);
        }
    }

    internal bool ByKeyboard { get; private set; }

    /// <summary>What a screen reader says: the caption and, when there is one, the counter.</summary>
    internal string AccessibleLabel => _badge > 0 ? Caption + ", " + BadgeText() + " en cours" : Caption;

    internal void Activate(bool byKeyboard)
    {
        ByKeyboard = byKeyboard;
        Invoked?.Invoke(this, EventArgs.Empty);
    }

    protected override AccessibleObject CreateAccessibilityInstance() => new ItemAccessibility(this);

    protected override bool IsInputKey(Keys keyData) => (keyData & Keys.KeyCode) is Keys.Up or Keys.Down || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.KeyCode)
        {
            case Keys.Enter or Keys.Space when e.Modifiers == Keys.None:
                e.Handled = e.SuppressKeyPress = true;
                Activate(byKeyboard: true);
                break;
            case Keys.Up or Keys.Down when e.Modifiers == Keys.None:
                e.Handled = e.SuppressKeyPress = true;
                Parent?.SelectNextControl(this, forward: e.KeyCode == Keys.Down, tabStopOnly: true, nested: false, wrap: true);
                break;
            case Keys.Apps when e.Modifiers == Keys.None:
            case Keys.F10 when e.Modifiers == Keys.Shift:
                e.Handled = e.SuppressKeyPress = true;
                ByKeyboard = true;
                Alternate?.Invoke(this, EventArgs.Empty);
                break;
        }
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        _hot = true;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hot = false;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;

        _pressed = true;
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        var wasPressed = _pressed;
        _pressed = false;
        Invalidate();
        if (!ClientRectangle.Contains(e.Location)) return;

        if (e.Button == MouseButtons.Left && wasPressed) Activate(byKeyboard: false);
        else if (e.Button == MouseButtons.Right)
        {
            ByKeyboard = false;
            Alternate?.Invoke(this, EventArgs.Empty);
        }
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if (!_pressed) return;

        _pressed = false;
        Invalidate();
    }

    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        Invalidate();
    }

    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        EnsureMetrics();
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var pill = PillBounds();
        if (_hot || _pressed)
        {
            using var highlight = new SolidBrush(Color.FromArgb(_pressed ? 0x33 : 0x16, Theme.Accent));
            using var shape = Rounded(new RectangleF(Px(6f), Px(2f), Width - Px(12f), Height - Px(4f)), Px(12f));
            g.FillPath(highlight, shape);
        }

        if (_selected)
        {
            using var fill = new SolidBrush(Color.FromArgb(0x2B, Theme.Accent));
            using var shape = Rounded(pill, pill.Height / 2f);
            g.FillPath(fill, shape);
        }

        var color = _selected ? Theme.Accent : Theme.Muted;
        DrawIcon(g, pill, color);
        DrawLabel(g, pill, color);
        DrawBadge(g, pill);

        if (Focused && ShowFocusCues)
        {
            using var ring = new Pen(Theme.Accent, Px(2f)) { Alignment = PenAlignment.Inset };
            using var shape = Rounded(new RectangleF(Px(3f), Px(1f), Width - Px(6f), Height - Px(2f)), Px(12f));
            g.DrawPath(ring, shape);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _labelFont?.Dispose();
            _selectedFont?.Dispose();
            _badgeFont?.Dispose();
            _iconPath?.Dispose();
        }

        base.Dispose(disposing);
    }

    private static GraphicsPath Rounded(RectangleF bounds, float radius)
    {
        var path = new GraphicsPath();
        var diameter = Math.Min(radius * 2f, Math.Min(bounds.Width, bounds.Height));
        if (diameter <= 0f)
        {
            path.AddRectangle(bounds);
            return path;
        }

        var arc = new RectangleF(bounds.X, bounds.Y, diameter, diameter);
        path.AddArc(arc, 180, 90);
        arc.X = bounds.Right - diameter;
        path.AddArc(arc, 270, 90);
        arc.Y = bounds.Bottom - diameter;
        path.AddArc(arc, 0, 90);
        arc.X = bounds.X;
        path.AddArc(arc, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>A size in dp, in device pixels.</summary>
    private float Px(float dp) => dp * DeviceDpi / 96f;

    private RectangleF PillBounds()
    {
        var width = Px(PillWidth);
        return new RectangleF((Width - width) / 2f, Px(TopMargin), width, Px(PillHeight));
    }

    private void EnsureMetrics()
    {
        if (_metricsDpi == DeviceDpi) return;

        _metricsDpi = DeviceDpi;
        _labelFont?.Dispose();
        _selectedFont?.Dispose();
        _badgeFont?.Dispose();
        _iconPath?.Dispose();
        _iconPath = null;

        // In pixels: the size does not depend on how the system converts points.
        _labelFont = new Font("Segoe UI", Px(12f), FontStyle.Regular, GraphicsUnit.Pixel);
        _selectedFont = new Font("Segoe UI", Px(12f), FontStyle.Bold, GraphicsUnit.Pixel);
        _badgeFont = new Font("Segoe UI", Px(11f), FontStyle.Bold, GraphicsUnit.Pixel);
    }

    private void DrawIcon(Graphics g, RectangleF pill, Color color)
    {
        var size = Px(IconSize);
        _iconPath ??= NavIconPaths.Build(_icon, new RectangleF(0f, 0f, size, size));

        using var pen = new Pen(color, NavIcons.StrokeWidth * size / NavIcons.Grid)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round,
        };
        var state = g.Save();
        g.TranslateTransform(pill.X + (pill.Width - size) / 2f, pill.Y + (pill.Height - size) / 2f);
        g.DrawPath(pen, _iconPath);
        g.Restore(state);
    }

    private void DrawLabel(Graphics g, RectangleF pill, Color color)
    {
        var font = _selected ? _selectedFont : _labelFont;
        var top = (int)Math.Round(pill.Bottom + Px(LabelGap));
        var margin = (int)Math.Round(Px(2f));
        var bounds = new Rectangle(margin, top, Math.Max(1, Width - margin * 2), Math.Max(1, font.Height));
        TextRenderer.DrawText(
            g,
            Caption,
            font,
            bounds,
            color,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
    }

    private void DrawBadge(Graphics g, RectangleF pill)
    {
        if (_badge <= 0) return;

        var text = BadgeText();
        var textWidth = TextRenderer.MeasureText(g, text, _badgeFont, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width;
        var height = Px(BadgeHeight);
        var width = Math.Max(height, textWidth + Px(8f));
        var bounds = new RectangleF(pill.X + Px(BadgeOffset), pill.Y, width, height);

        using (var shape = Rounded(bounds, height / 2f))
        using (var fill = new SolidBrush(Theme.Accent))
        using (var outline = new Pen(Theme.Bar, Px(2f)) { Alignment = PenAlignment.Inset })
        {
            g.FillPath(fill, shape);
            g.DrawPath(outline, shape);
        }

        TextRenderer.DrawText(
            g,
            text,
            _badgeFont,
            Rectangle.Round(bounds),
            Theme.Ink,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoClipping);
    }

    private string BadgeText() => _badge > 99 ? "99+" : _badge.ToString(CultureInfo.InvariantCulture);

    private sealed class ItemAccessibility(NavItem owner) : ControlAccessibleObject(owner)
    {
        public override AccessibleRole Role => AccessibleRole.PageTab;

        public override string? Name
        {
            get => owner.AccessibleLabel;
            set { }
        }

        public override AccessibleStates State => base.State | AccessibleStates.Selectable | (owner.Selected ? AccessibleStates.Selected : AccessibleStates.None);

        public override string DefaultAction => "Ouvrir";

        public override void DoDefaultAction() => owner.Activate(byKeyboard: true);
    }
}
