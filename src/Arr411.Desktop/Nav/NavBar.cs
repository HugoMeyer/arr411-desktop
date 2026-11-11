using System.ComponentModel;
using Arr411.Core.Drawing;
using Arr411.Core.Shell;

namespace Arr411.Desktop.Nav;

/// <summary>
/// The navigation bar on the left of the window: the five tabs of the Android bar, top to bottom, and the menu of the app at the bottom.
/// Clicking a tab shows it; clicking the current one, or right-clicking, does what the Android long press does.
/// </summary>
internal sealed class NavBar : Panel
{
    private const float BarWidth = 76f;
    private const float ItemHeight = 64f;
    private const float EdgeMargin = 8f;

    private static readonly Dictionary<string, string> Captions = new(StringComparer.Ordinal)
    {
        [Tabs.C411] = "C411",
        [Tabs.Radarr] = "Radarr",
        [Tabs.Sonarr] = "Sonarr",
        [Tabs.Lidarr] = "Lidarr",
        [Tabs.Qbit] = "qBittorrent",
    };

    private readonly Dictionary<string, NavItem> _items = new(StringComparer.Ordinal);
    private readonly NavItem _menu;
    private readonly ToolTip _tips = new() { ShowAlways = true };
    private string _selected = "";

    public NavBar()
    {
        // Adding the items would lay the bar out before every one of them exists.
        SuspendLayout();
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Bar;
        Dock = DockStyle.Left;
        AccessibleName = "Navigation";
        AccessibleRole = AccessibleRole.PageTabList;

        var index = 0;
        foreach (var tab in Tabs.Bar)
        {
            var item = new NavItem(tab, Captions[tab], NavIcons.For(tab) ?? throw new InvalidOperationException("No icon for " + tab)) { TabIndex = index };
            item.Invoked += (_, _) => TabInvoked?.Invoke(tab, tab == _selected, item.ByKeyboard);
            item.Alternate += (_, _) => TabAlternate?.Invoke(tab, item.ByKeyboard);
            _items[tab] = item;
            _tips.SetToolTip(item, Captions[tab] + " (Ctrl+" + (index + 1) + ")");
            Controls.Add(item);
            index++;
        }

        _menu = new NavItem("menu", "Menu", NavIconPaths.Menu) { TabIndex = index };
        _menu.Invoked += (_, _) => MenuRequested?.Invoke(_menu.ByKeyboard);
        _menu.Alternate += (_, _) => MenuRequested?.Invoke(_menu.ByKeyboard);
        _tips.SetToolTip(_menu, "Menu d'Arr411");
        Controls.Add(_menu);

        ResumeLayout(performLayout: false);
        ApplyDpi();
    }

    /// <summary>A tab was clicked (or opened with the keyboard): its name, whether it was already the current one, and whether it was the keyboard.</summary>
    public event Action<string, bool, bool>? TabInvoked;

    /// <summary>The secondary action of a tab (right click, menu key, long press): its name, and whether it was the keyboard.</summary>
    public event Action<string, bool>? TabAlternate;

    /// <summary>The menu button was used; true if it was the keyboard.</summary>
    public event Action<bool>? MenuRequested;

    /// <summary>The highlighted tab. Any other value (the settings page has no tab) leaves every tab unselected.</summary>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Selected
    {
        get => _selected;
        set
        {
            _selected = value ?? "";
            foreach (var (tab, item) in _items) item.Selected = tab == _selected;
        }
    }

    /// <summary>The number of active downloads, shown over the qBittorrent tab.</summary>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Badge
    {
        get => _items[Tabs.Qbit].Badge;
        set => _items[Tabs.Qbit].Badge = value;
    }

    /// <summary>The button of a tab, to anchor a menu to it.</summary>
    public Control ItemFor(string tab) => _items.TryGetValue(tab, out var item) ? item : _menu;

    /// <summary>The menu button.</summary>
    public Control MenuButton => _menu;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyDpi();
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        ApplyDpi();
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        var width = Math.Max(1, Width - 1);
        var height = Px(ItemHeight);
        var y = Px(EdgeMargin);
        foreach (var tab in Tabs.Bar)
        {
            _items[tab].SetBounds(0, y, width, height);
            y += height;
        }

        _menu.SetBounds(0, Math.Max(y, Height - height - Px(EdgeMargin)), width, height);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var brush = new SolidBrush(Theme.Raised);
        e.Graphics.FillRectangle(brush, Width - 1, 0, 1, Height);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _tips.Dispose();
        base.Dispose(disposing);
    }

    private int Px(float dp) => (int)Math.Round(dp * DeviceDpi / 96f);

    private void ApplyDpi()
    {
        var width = Px(BarWidth);
        if (Width != width) Width = width;
        PerformLayout();
        Invalidate(true);
    }
}
