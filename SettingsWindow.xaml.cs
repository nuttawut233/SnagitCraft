using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using SnagitAnything.Capture;
using SnagitAnything.Services;

namespace SnagitAnything;

/// <summary>One hotkey row in Settings (bindable).</summary>
public sealed class HotkeyRow : INotifyPropertyChanged
{
    private HotkeyBinding? _binding;
    private bool _recording;

    public required CaptureProfile Profile { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }

    public HotkeyBinding? Binding
    {
        get => _binding;
        set { _binding = value; Changed(); }
    }

    public bool Recording
    {
        get => _recording;
        set { _recording = value; Changed(); }
    }

    public string Display => Recording ? "กดปุ่มที่ต้องการ…" : Binding?.Display ?? "— ไม่มี —";
    public Brush Foreground => Recording ? Brushes.White : Binding is null ? Brushes.Gray : new SolidColorBrush(Color.FromRgb(0x1F, 0x23, 0x28));
    public Brush Background => Recording ? new SolidColorBrush(Color.FromRgb(0xE8, 0x5D, 0x0C)) : new SolidColorBrush(Color.FromRgb(0xF4, 0xF5, 0xF7));
    public Brush Border => Recording ? new SolidColorBrush(Color.FromRgb(0xC9, 0x4F, 0x09)) : new SolidColorBrush(Color.FromRgb(0xD0, 0xD4, 0xD9));

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Changed()
    {
        foreach (var p in new[] { nameof(Binding), nameof(Recording), nameof(Display), nameof(Foreground), nameof(Background), nameof(Border) })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
    }
}

public partial class SettingsWindow : Window
{
    private readonly App _app;
    private readonly List<HotkeyRow> _rows;

    public SettingsWindow(App app)
    {
        _app = app;
        InitializeComponent();
        _rows = CaptureProfiles.All.Select(p => new HotkeyRow
        {
            Profile = p.Profile, Name = $"{p.Icon}  {p.Name}", Description = p.Description,
            Binding = app.Settings.HotkeyFor(p.Profile),
        }).ToList();
        Rows.ItemsSource = _rows;
        WidgetBox.IsChecked = app.Settings.ShowCaptureWidget;
        AutoHideBox.IsChecked = app.Settings.WidgetAutoHide;
        MinimizedBox.IsChecked = app.Settings.StartMinimized;
    }

    private void Record_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not HotkeyRow row) return;
        foreach (var r in _rows) r.Recording = false;
        row.Recording = true;
        Message.Text = "";
        // The global hook reports the next combination — the window itself never sees Print Screen.
        _app.Hotkeys?.RecordNext(binding =>
        {
            row.Recording = false;
            if (binding is null) return; // Esc
            if (binding.Modifiers == System.Windows.Input.ModifierKeys.None && !WorksAlone(binding.Key))
            {
                Message.Text = $"\"{binding.Display}\" ใช้เดี่ยวๆ ไม่ได้ (จะพิมพ์ตัวนั้นไม่ได้อีก) — ใส่ Ctrl / Alt / Shift / Win ร่วมด้วย";
                return;
            }
            var clash = _rows.FirstOrDefault(r => r != row && r.Binding == binding);
            if (clash is not null)
            {
                clash.Binding = null;
                Message.Text = $"ย้าย {binding.Display} มาจาก \"{clash.Name.Trim()}\" แล้ว";
            }
            row.Binding = binding;
        });
    }

    /// <summary>Keys that can be a hotkey without modifiers, because nobody types with them.</summary>
    private static bool WorksAlone(System.Windows.Input.Key key) =>
        key is System.Windows.Input.Key.Snapshot or System.Windows.Input.Key.Pause or System.Windows.Input.Key.Scroll ||
        (key >= System.Windows.Input.Key.F1 && key <= System.Windows.Input.Key.F24);

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is HotkeyRow row)
        {
            _app.Hotkeys?.CancelRecording();
            row.Recording = false;
            row.Binding = null;
        }
    }

    private void Defaults_Click(object sender, RoutedEventArgs e)
    {
        _app.Hotkeys?.CancelRecording();
        foreach (var r in _rows)
        {
            r.Recording = false;
            r.Binding = HotkeyBinding.Parse(CaptureProfiles.Get(r.Profile).DefaultHotkey);
        }
        Message.Text = "";
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var s = _app.Settings;
        s.Hotkeys = _rows.ToDictionary(r => r.Profile.ToString(), r => r.Binding?.ToString() ?? "");
        s.ShowCaptureWidget = WidgetBox.IsChecked == true;
        s.WidgetAutoHide = AutoHideBox.IsChecked == true;
        s.StartMinimized = MinimizedBox.IsChecked == true;
        s.Save();
        _app.ApplySettings();
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        _app.Hotkeys?.CancelRecording();
        base.OnClosed(e);
    }
}
