using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using SnagitAnything.Capture;
using SnagitAnything.Services;

namespace SnagitAnything;

public sealed record RecentItem(string Path, BitmapSource? Thumbnail, string Caption);

public partial class MainWindow : Window
{
    private readonly App _app;
    private bool _loading = true;

    public MainWindow(App app)
    {
        _app = app;
        InitializeComponent();
        LoadOptions();
        RefreshRecent();
        _loading = false;
    }

    public void SetHotkeyStatus(string text) => HotkeyStatus.Text = text;

    private AppSettings Settings => _app.Settings;

    private void LoadOptions()
    {
        SelectByTag(DelayBox, Settings.DelaySeconds.ToString());
        SelectByTag(FormatBox, Settings.ImageFormat);
        OpenEditorBox.IsChecked = Settings.OpenEditor;
        ClipboardBox.IsChecked = Settings.CopyToClipboard;
        AutoSaveBox.IsChecked = Settings.AutoSave;
        FolderText.Text = Settings.SaveFolder;
    }

    private static void SelectByTag(ComboBox box, string tag) =>
        box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == tag) ?? box.Items[0];

    private void Option_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Settings.DelaySeconds = int.Parse((string)((ComboBoxItem)DelayBox.SelectedItem).Tag);
        Settings.ImageFormat = (string)((ComboBoxItem)FormatBox.SelectedItem).Tag;
        Settings.OpenEditor = OpenEditorBox.IsChecked == true;
        Settings.CopyToClipboard = ClipboardBox.IsChecked == true;
        Settings.AutoSave = AutoSaveBox.IsChecked == true;
        Settings.Save();
    }

    public void RefreshRecent()
    {
        var items = _app.Library.Recent().Select(f =>
        {
            BitmapSource? thumb = null;
            try { thumb = CaptureLibrary.Load(f.FullName, decodeWidth: 320); }
            catch (Exception ex) { Log.Error("Thumbnail failed for " + f.FullName, ex); }
            return new RecentItem(f.FullName, thumb, $"{f.LastWriteTime:dd/MM HH:mm:ss}  •  {f.Name}");
        }).ToList();
        RecentList.ItemsSource = items;
        RecentTitle.Text = $"ภาพล่าสุด ({items.Count})";
        EmptyText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Capture_Click(object sender, RoutedEventArgs e) => _app.StartCapture(CaptureProfile.Image);
    private void CaptureMonitor_Click(object sender, RoutedEventArgs e) => _app.StartCapture(CaptureProfile.Monitor);
    private void CaptureFull_Click(object sender, RoutedEventArgs e) => _app.StartCapture(CaptureProfile.FullScreen);
    private void Settings_Click(object sender, RoutedEventArgs e) => _app.OpenSettings();
    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshRecent();

    private void ChangeFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { InitialDirectory = Settings.SaveFolder, Title = "เลือกโฟลเดอร์เก็บภาพ" };
        if (dlg.ShowDialog(this) != true) return;
        Settings.SaveFolder = dlg.FolderName;
        Settings.Save();
        FolderText.Text = dlg.FolderName;
        RefreshRecent();
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(Settings.SaveFolder);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Settings.SaveFolder}\"") { UseShellExecute = true });
    }

    private RecentItem? Current => RecentList.SelectedItem as RecentItem;

    private void Recent_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) => RecentOpen_Click(sender, e);

    private void RecentOpen_Click(object sender, RoutedEventArgs e)
    {
        if (Current is { } item) _app.OpenEditor(item.Path);
    }

    private void RecentCopy_Click(object sender, RoutedEventArgs e)
    {
        if (Current is not { } item) return;
        try { ClipboardHelper.SetImage(CaptureLibrary.Load(item.Path)); }
        catch (Exception ex) { Log.Error("Copy failed", ex); }
    }

    private void RecentReveal_Click(object sender, RoutedEventArgs e)
    {
        if (Current is { } item)
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{item.Path}\"") { UseShellExecute = true });
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Closing the window keeps the app in the tray, like Snagit.
        if (!_app.IsExiting)
        {
            e.Cancel = true;
            Hide();
            _app.NotifyOnce("SnagitCraft ยังทำงานอยู่ที่ถาดระบบ", "กด Print Screen เพื่อจับภาพได้ตลอด");
        }
        base.OnClosing(e);
    }
}
