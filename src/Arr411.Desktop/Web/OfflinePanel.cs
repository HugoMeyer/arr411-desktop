namespace Arr411.Desktop.Web;

/// <summary>Covers the C411 tab when the site cannot be reached (usually: no VPN) and offers to try again.</summary>
internal sealed class OfflinePanel : Panel
{
    public const string Message = "Impossible de joindre C411.\r\nVérifie ta connexion et que le VPN est activé.";
    public const string RetryText = "Réessayer";

    private const int MessageWidth = 520;
    private const int MessageHeight = 64;

    private readonly Label _message = new()
    {
        AutoSize = false,
        Text = Message,
        TextAlign = ContentAlignment.MiddleCenter,
        Font = new Font("Segoe UI", 12F),
        ForeColor = Theme.Text,
    };

    private readonly Button _retry = new()
    {
        Text = RetryText,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        FlatStyle = FlatStyle.Flat,
        Font = new Font("Segoe UI", 10F, FontStyle.Bold),
        BackColor = Theme.Accent,
        ForeColor = Theme.Ink,
        Padding = new Padding(14, 6, 14, 6),
        UseVisualStyleBackColor = false,
    };

    public OfflinePanel()
    {
        BackColor = Theme.Background;
        _retry.FlatAppearance.BorderSize = 0;
        _retry.Click += (_, _) => RetryRequested?.Invoke(this, EventArgs.Empty);
        Controls.Add(_message);
        Controls.Add(_retry);
    }

    public event EventHandler? RetryRequested;

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        var width = Math.Min(MessageWidth, Math.Max(0, ClientSize.Width - 32));
        var top = Math.Max(0, (ClientSize.Height - MessageHeight - _retry.Height - 16) / 2);
        _message.SetBounds((ClientSize.Width - width) / 2, top, width, MessageHeight);
        _retry.Location = new Point(Math.Max(0, (ClientSize.Width - _retry.Width) / 2), _message.Bottom + 16);
    }
}
