using System;
using System.Drawing;
using System.Windows.Forms;

namespace SnagitAnything.Services;

/// <summary>Notification-area icon with the capture menu.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly Icon _image;

    public TrayIcon(Action capture, Action fullScreen, Action open, Action exit, Action settings)
    {
        _image = LoadIcon();
        var menu = new ContextMenuStrip();
        menu.Items.Add("จับภาพ", null, (_, _) => capture());
        menu.Items.Add("จับภาพเต็มจอ", null, (_, _) => fullScreen());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("เปิด SnagitCraft", null, (_, _) => open());
        menu.Items.Add("ตั้งค่า / ปุ่มลัด…", null, (_, _) => settings());
        menu.Items.Add("ออกจากโปรแกรม", null, (_, _) => exit());

        _icon = new NotifyIcon { Icon = _image, Text = "SnagitCraft", ContextMenuStrip = menu, Visible = true };
        _icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) capture(); };
        _icon.DoubleClick += (_, _) => open();
    }

    public void Notify(string title, string text) =>
        _icon.ShowBalloonTip(2500, title, text, ToolTipIcon.Info);

    /// <summary>The tiger logo from Assetspp.ico, at the tray size for the current DPI (16 px at 100 %).</summary>
    public static Icon LoadIcon()
    {
        var info = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"));
        using var stream = info!.Stream;
        return new Icon(stream, SystemInformation.SmallIconSize);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _image.Dispose();
    }
}
