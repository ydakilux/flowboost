using System.Windows.Forms;

namespace flowboost.Tray;

public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _signIn;
    private readonly ToolStripMenuItem _autostart;
    private readonly ContextMenuStrip _menu;
    private readonly Icon _trayImage;
    private readonly Action<bool> _autostartChanged;

    public TrayIcon(Action openSettings, Action signIn, Action<bool> autostart, Action quit)
    {
        _autostartChanged = autostart;
        _menu = new ContextMenuStrip();
        var menu = _menu;
        menu.Items.Add("Settings", null, (_, _) => openSettings());
        _signIn = new ToolStripMenuItem("Sign in", null, (_, _) => signIn());
        _autostart = new ToolStripMenuItem("Start with Windows") { CheckOnClick = true };
        _autostart.Click += (_, _) => _autostartChanged(_autostart.Checked);
        menu.Items.Add(_signIn); menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_autostart); menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => quit());
        _trayImage = CreateIcon();
        _icon = new NotifyIcon { Icon = _trayImage, Text = "flowboost", ContextMenuStrip = menu, Visible = true };
        _icon.DoubleClick += (_, _) => openSettings();
    }

    public void Update(bool signedIn, bool autostart)
    {
        _signIn.Enabled = true; _autostart.Checked = autostart;
    }

    public void Balloon(string title, string text) => _icon.ShowBalloonTip(4000, title, text, ToolTipIcon.Info);
    public void Dispose() { _icon.Visible = false; _icon.Dispose(); _trayImage.Dispose(); _menu.Dispose(); }

    private static Icon CreateIcon()
    {
        using var stream = typeof(TrayIcon).Assembly.GetManifestResourceStream("flowboost.public.flowboost.ico")
            ?? throw new InvalidOperationException("The flowboost tray icon resource is missing.");
        using var resourceIcon = new Icon(stream);
        return (Icon)resourceIcon.Clone();
    }
}
