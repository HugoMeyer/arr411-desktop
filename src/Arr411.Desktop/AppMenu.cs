using System.Drawing.Drawing2D;

namespace Arr411.Desktop;

/// <summary>What the menus of the app ask of the window.</summary>
internal interface IAppMenuHost
{
    /// <summary>Brings the window to the front, from the notification area.</summary>
    void OpenWindow();

    /// <summary>Opens the settings page of the web app.</summary>
    void OpenSettings();

    /// <summary>Lets Windows offer Arr411 for magnet links and .torrent files, and opens the page where the user picks it.</summary>
    void Associate();

    /// <summary>Whether the app starts when the user signs in to Windows.</summary>
    bool StartWithWindows { get; set; }

    /// <summary>Whether closing the window keeps the app running in the notification area.</summary>
    bool CloseToTray { get; set; }

    void OpenLogs();

    void ShowAbout();

    void Quit();
}

/// <summary>The menus of the app: the one of the menu button of the tab bar, the one of the notification area, the one of the C411 tab.</summary>
internal static class AppMenu
{
    /// <summary>The menu of the app. <paramref name="tray"/> adds the entry that brings the window back, for the notification area.</summary>
    public static ContextMenuStrip Build(IAppMenuHost host, bool tray)
    {
        var menu = NewMenu();
        if (tray)
        {
            var open = Item("Ouvrir Arr411", host.OpenWindow);
            var bold = new Font(menu.Font, FontStyle.Bold);
            open.Font = bold;
            menu.Disposed += (_, _) => bold.Dispose();
            menu.Items.Add(open);
        }

        menu.Items.Add(Item("Réglages Arr411", host.OpenSettings));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Item("Associer aux liens magnet et fichiers .torrent…", host.Associate));

        // No CheckOnClick: the host may refuse (the registry can be locked down), and the box must show what is true.
        var startup = Item("Lancer avec Windows", () => host.StartWithWindows = !host.StartWithWindows);
        var keepRunning = Item("Réduire dans la zone de notification à la fermeture", () => host.CloseToTray = !host.CloseToTray);
        menu.Items.Add(startup);
        menu.Items.Add(keepRunning);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Item("Ouvrir le dossier des journaux", host.OpenLogs));
        menu.Items.Add(Item("À propos d'Arr411", host.ShowAbout));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Item("Quitter", host.Quit));

        menu.Opening += (_, _) =>
        {
            startup.Checked = host.StartWithWindows;
            keepRunning.Checked = host.CloseToTray;
        };
        return menu;
    }

    /// <summary>The menu of the C411 tab (what the Android app shows on a long press): reload, home, copy the link, settings.</summary>
    /// <param name="hasLink">Whether the page on screen has a link to copy.</param>
    public static ContextMenuStrip BuildC411(Action reload, Action home, Func<bool> hasLink, Action copyLink, Action settings)
    {
        var menu = NewMenu();
        var copy = Item("Copier le lien", copyLink);
        menu.Items.Add(Item("Recharger la page", reload));
        menu.Items.Add(Item("Accueil C411", home));
        menu.Items.Add(copy);
        menu.Items.Add(Item("Réglages Arr411", settings));
        menu.Opening += (_, _) => copy.Enabled = hasLink();
        return menu;
    }

    private static ContextMenuStrip NewMenu() =>
        new()
        {
            Renderer = new DarkMenuRenderer(),
            ShowImageMargin = false,
            ShowCheckMargin = true,
            ShowItemToolTips = false,
            BackColor = Theme.Raised,
            ForeColor = Theme.Text,
            AccessibleName = "Menu d'Arr411",
        };

    private static ToolStripMenuItem Item(string text, Action action)
    {
        var item = new ToolStripMenuItem(text) { ForeColor = Theme.Text };
        item.Click += (_, _) => action();
        return item;
    }
}

/// <summary>Draws the menus in the colours of the app instead of the ones of Windows.</summary>
internal sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
{
    public DarkMenuRenderer() => RoundedEdges = false;

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        using var brush = new SolidBrush(Theme.Raised);
        e.Graphics.FillRectangle(brush, e.AffectedBounds);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        using var pen = new Pen(Theme.Line);
        e.Graphics.DrawRectangle(pen, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
    }

    // The margin that holds the icons: nothing, the check boxes sit on the same background.
    protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
    {
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        var item = e.Item;
        if (!item.Selected || !item.Enabled) return;

        var inset = Px(item, 2);
        using var brush = new SolidBrush(Theme.Hover);
        e.Graphics.FillRectangle(brush, inset, 1, item.Width - inset * 2, item.Height - 2);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        var inset = Px(e.Item, 8);
        var y = e.Item.Height / 2;
        using var pen = new Pen(Theme.Line);
        e.Graphics.DrawLine(pen, inset, y, e.Item.Width - inset, y);
    }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        var box = e.ImageRectangle;
        var g = e.Graphics;
        var smoothing = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        using var pen = new Pen(Theme.Accent, Math.Max(1.5f, box.Height / 9f))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round,
        };
        g.DrawLines(
            pen,
            [
                new PointF(box.X + box.Width * 0.22f, box.Y + box.Height * 0.54f),
                new PointF(box.X + box.Width * 0.43f, box.Y + box.Height * 0.74f),
                new PointF(box.X + box.Width * 0.80f, box.Y + box.Height * 0.28f),
            ]);
        g.SmoothingMode = smoothing;
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? Theme.Text : Theme.Muted;
        base.OnRenderItemText(e);
    }

    private static int Px(ToolStripItem item, int dp) => (int)Math.Round(dp * (item.Owner?.DeviceDpi ?? 96) / 96.0);
}
