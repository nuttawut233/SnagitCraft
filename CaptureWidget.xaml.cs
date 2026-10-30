using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using SnagitAnything.Capture;
using SnagitAnything.Services;
using static SnagitAnything.Native.NativeMethods;

namespace SnagitAnything;

public sealed record ProfileRow(CaptureProfile Profile, string Icon, string Name, string Hotkey, string Description);

/// <summary>
/// Always-on-top capture panel docked to a screen edge (like Snagit's capture widget): pick a profile,
/// press the red button. Drag it to any of the four edges to move it there. With auto-hide on it tucks
/// into the edge, leaving only the blue tab, and slides out when the mouse comes near.
/// </summary>
public partial class CaptureWidget : Window
{
    private const double StripThickness = 20;
    private const double HiddenSliver = 6;   // DIP of the blue strip left on screen when tucked away
    private const int PopOutDistance = 28;   // px from the tab that counts as "near"
    private const int StayOpenMargin = 16;   // px around the open widget that still counts as inside
    private static readonly TimeSpan HideDelay = TimeSpan.FromMilliseconds(700);
    private static readonly TimeSpan HideDelayWithList = TimeSpan.FromSeconds(2);

    private readonly App _app;
    private readonly DispatcherTimer _timer;
    private bool _loading = true;
    private bool _hidden;
    private bool _dragging;
    private DateTime _lastNear = DateTime.Now;
    private Point? _pressAt;
    private bool _pressOnStrip;

    public CaptureWidget(App app)
    {
        _app = app;
        InitializeComponent();
        RefreshProfiles();
        ApplyEdge(_app.Settings.WidgetEdge);
        _loading = false;

        SizeChanged += (_, _) => Place();
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(80), DispatcherPriority.Background, (_, _) => Tick(), Dispatcher);
        _timer.Start();
        IsVisibleChanged += (_, _) => { if (IsVisible) { _lastNear = DateTime.Now; SlideOut(animate: false); } };
    }

    private DockEdge Edge => _app.Settings.WidgetEdge;
    private bool Vertical => Edge is DockEdge.Left or DockEdge.Right; // a column on the left/right edges

    public void RefreshProfiles()
    {
        bool was = _loading;
        _loading = true;
        Profiles.ItemsSource = CaptureProfiles.All.Select(p =>
            new ProfileRow(p.Profile, p.Icon, p.Name, _app.Settings.HotkeyFor(p.Profile)?.Display ?? "", p.Description)).ToList();
        Profiles.SelectedItem = Profiles.Items.OfType<ProfileRow>().FirstOrDefault(r => r.Profile == _app.Settings.WidgetProfile);
        var current = CaptureProfiles.Get(_app.Settings.WidgetProfile);
        string key = _app.Settings.HotkeyFor(current.Profile)?.Display is { Length: > 0 } k ? $"  ({k})" : "";
        CaptureButton.ToolTip = $"จับภาพ: {current.Name}{key}";
        _loading = was;
    }

    // ------------------------------------------------------------------ layout per edge

    /// <summary>Arranges profiles / strip / buttons so the buttons face the screen edge and the strip sits next to them.</summary>
    private void ApplyEdge(DockEdge edge)
    {
        Layout.RowDefinitions.Clear();
        Layout.ColumnDefinitions.Clear();
        UIElement[] order = edge switch
        {
            DockEdge.Right => new UIElement[] { ProfilesPanel, Strip, Buttons },
            DockEdge.Left => new UIElement[] { Buttons, Strip, ProfilesPanel },
            DockEdge.Top => new UIElement[] { Buttons, Strip, ProfilesPanel },
            _ => new UIElement[] { ProfilesPanel, Strip, Buttons },
        };
        bool vertical = edge is DockEdge.Left or DockEdge.Right;
        for (int i = 0; i < order.Length; i++)
        {
            if (vertical)
            {
                Layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                Grid.SetColumn(order[i], i);
                Grid.SetRow(order[i], 0);
            }
            else
            {
                Layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                Grid.SetRow(order[i], i);
                Grid.SetColumn(order[i], 0);
            }
        }

        Strip.Width = vertical ? StripThickness : double.NaN;
        Strip.Height = vertical ? double.NaN : StripThickness;
        Buttons.Orientation = vertical ? Orientation.Vertical : Orientation.Horizontal;
        Buttons.Margin = vertical ? new Thickness(8, 10, 8, 10) : new Thickness(14, 8, 14, 8);
        CaptureButton.Margin = vertical ? new Thickness(0, 12, 0, 12) : new Thickness(14, 0, 14, 0);

        // Flat, borderless side against the screen edge; rounded corners and shadow room elsewhere.
        const double r = 8, m = 8;
        (Root.CornerRadius, Root.Margin, Root.BorderThickness) = edge switch
        {
            DockEdge.Right => (new CornerRadius(r, 0, 0, r), new Thickness(m, m, 0, m), new Thickness(1, 1, 0, 1)),
            DockEdge.Left => (new CornerRadius(0, r, r, 0), new Thickness(0, m, m, m), new Thickness(0, 1, 1, 1)),
            DockEdge.Top => (new CornerRadius(0, 0, r, r), new Thickness(m, 0, m, m), new Thickness(1, 0, 1, 1)),
            _ => (new CornerRadius(r, r, 0, 0), new Thickness(m, m, m, 0), new Thickness(1, 1, 1, 0)),
        };
        Slide.X = Slide.Y = 0;
        _hidden = false;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Place();
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        Dispatcher.BeginInvoke(Place, DispatcherPriority.Loaded);
    }

    private IntPtr Handle => new WindowInteropHelper(this).Handle;

    private RECT WorkArea()
    {
        var s = _app.Settings;
        var anchor = s.WidgetMonitorX is { } x && s.WidgetMonitorY is { } y ? new POINT { X = x, Y = y } : new POINT { X = 0, Y = 0 };
        return GetWorkAreaAt(anchor); // nearest monitor, so a disconnected screen falls back sensibly
    }

    /// <summary>Puts the window flush against its edge, at the saved position along that edge.</summary>
    private void Place()
    {
        if (_dragging || Handle == IntPtr.Zero) return;
        GetWindowRect(Handle, out var r);
        int w = r.Width, h = r.Height;
        var work = WorkArea();
        double t = Math.Clamp(_app.Settings.WidgetOffset, 0, 1);
        int x, y;
        if (Vertical)
        {
            x = Edge == DockEdge.Right ? work.Right - w : work.Left;
            y = Math.Clamp((int)(work.Top + t * work.Height - h / 2.0), work.Top, Math.Max(work.Top, work.Bottom - h));
        }
        else
        {
            y = Edge == DockEdge.Bottom ? work.Bottom - h : work.Top;
            x = Math.Clamp((int)(work.Left + t * work.Width - w / 2.0), work.Left, Math.Max(work.Left, work.Right - w));
        }
        if (x != r.Left || y != r.Top)
            SetWindowPos(Handle, HWND_TOPMOST, x, y, 0, 0, SWP_NOACTIVATE | 0x0001 /* SWP_NOSIZE */);
    }

    // ------------------------------------------------------------------ auto-hide

    /// <summary>How far the content slides toward the edge so only a thin sliver of the blue strip stays visible.</summary>
    private double HiddenDistance =>
        (Vertical ? Root.ActualWidth : Root.ActualHeight) - HiddenSliver;

    private void SlideIn()
    {
        if (_hidden) return;
        if (ProfilesPanel.Visibility == Visibility.Visible) SetProfilesOpen(false);
        UpdateLayout();
        double d = HiddenDistance;
        Animate(Edge switch { DockEdge.Right or DockEdge.Bottom => d, _ => -d });
        _hidden = true;
    }

    private void SlideOut(bool animate = true)
    {
        if (!_hidden && Slide.X == 0 && Slide.Y == 0) return;
        _hidden = false;
        if (animate) Animate(0);
        else
        {
            Slide.BeginAnimation(TranslateTransform.XProperty, null);
            Slide.BeginAnimation(TranslateTransform.YProperty, null);
            Slide.X = Slide.Y = 0;
        }
    }

    private void Animate(double to)
    {
        var anim = new DoubleAnimation(to, TimeSpan.FromMilliseconds(170)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
        var prop = Vertical ? TranslateTransform.XProperty : TranslateTransform.YProperty;
        var other = Vertical ? TranslateTransform.YProperty : TranslateTransform.XProperty;
        Slide.BeginAnimation(other, null);
        if (Vertical) Slide.Y = 0; else Slide.X = 0;
        Slide.BeginAnimation(prop, anim);
    }

    /// <summary>Polls the mouse: pop out when it comes near the tab, tuck away a moment after it leaves.</summary>
    private void Tick()
    {
        if (!IsVisible || _dragging || Handle == IntPtr.Zero) return;
        if (!_app.Settings.WidgetAutoHide)
        {
            if (_hidden) SlideOut();
            return;
        }

        GetCursorPos(out var p);
        GetWindowRect(Handle, out var r);
        int band = (int)Math.Ceiling(HiddenSliver * GetScaleOfWindowMonitor(Handle)) + PopOutDistance;
        // Hidden: only the strip along the edge is on screen, so look for the mouse near that band.
        var zone = !_hidden ? new RECT { Left = r.Left - StayOpenMargin, Top = r.Top - StayOpenMargin, Right = r.Right + StayOpenMargin, Bottom = r.Bottom + StayOpenMargin }
            : Edge switch
            {
                DockEdge.Right => new RECT { Left = r.Right - band, Top = r.Top - 10, Right = r.Right + 1, Bottom = r.Bottom + 10 },
                DockEdge.Left => new RECT { Left = r.Left - 1, Top = r.Top - 10, Right = r.Left + band, Bottom = r.Bottom + 10 },
                DockEdge.Top => new RECT { Left = r.Left - 10, Top = r.Top - 1, Right = r.Right + 10, Bottom = r.Top + band },
                _ => new RECT { Left = r.Left - 10, Top = r.Bottom - band, Right = r.Right + 10, Bottom = r.Bottom + 1 },
            };
        bool near = p.X >= zone.Left && p.X < zone.Right && p.Y >= zone.Top && p.Y < zone.Bottom;

        if (near)
        {
            _lastNear = DateTime.Now;
            if (_hidden) SlideOut();
            return;
        }
        if (_hidden || Mouse.LeftButton == MouseButtonState.Pressed) return;
        var delay = ProfilesPanel.Visibility == Visibility.Visible ? HideDelayWithList : HideDelay;
        if (DateTime.Now - _lastNear > delay) SlideIn();
    }

    // ------------------------------------------------------------------ dragging to another edge

    private void Root_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Buttons and the profile list keep their own clicks; everything else drags (or, on the strip, toggles).
        if (IsInside<ButtonBase>(e.OriginalSource) || IsInside<ListBox>(e.OriginalSource)) return;
        _pressAt = e.GetPosition(this);
        _pressOnStrip = IsWithin(e.OriginalSource, Strip);
        Root.CaptureMouse();
        Root.MouseMove += Root_MouseMove;
        Root.MouseLeftButtonUp += Root_MouseLeftButtonUp;
        e.Handled = true;
    }

    private void Root_MouseMove(object sender, MouseEventArgs e)
    {
        if (_pressAt is not { } start || e.LeftButton != MouseButtonState.Pressed) return;
        if ((e.GetPosition(this) - start).Length < 5) return;
        EndPress();
        _dragging = true;
        SlideOut(animate: false);
        try { DragMove(); }
        finally { _dragging = false; }
        DockToNearestEdge();
    }

    private void Root_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        bool click = _pressAt is not null && _pressOnStrip;
        EndPress();
        if (click) SetProfilesOpen(ProfilesPanel.Visibility != Visibility.Visible);
    }

    private void EndPress()
    {
        _pressAt = null;
        Root.MouseMove -= Root_MouseMove;
        Root.MouseLeftButtonUp -= Root_MouseLeftButtonUp;
        Root.ReleaseMouseCapture();
    }

    /// <summary>After a drag, snaps to whichever edge of that monitor the widget is closest to.</summary>
    private void DockToNearestEdge()
    {
        GetWindowRect(Handle, out var r);
        var center = new POINT { X = (r.Left + r.Right) / 2, Y = (r.Top + r.Bottom) / 2 };
        GetCursorPos(out var cursor);
        var work = GetWorkAreaAt(cursor); // the monitor the user dropped it on
        var distances = new (DockEdge Edge, int D)[]
        {
            (DockEdge.Left, Math.Abs(cursor.X - work.Left)), (DockEdge.Right, Math.Abs(work.Right - cursor.X)),
            (DockEdge.Top, Math.Abs(cursor.Y - work.Top)), (DockEdge.Bottom, Math.Abs(work.Bottom - cursor.Y)),
        };
        var edge = distances.OrderBy(d => d.D).First().Edge;

        var s = _app.Settings;
        s.WidgetEdge = edge;
        s.WidgetOffset = edge is DockEdge.Left or DockEdge.Right
            ? Math.Clamp((center.Y - work.Top) / (double)Math.Max(1, work.Height), 0, 1)
            : Math.Clamp((center.X - work.Left) / (double)Math.Max(1, work.Width), 0, 1);
        s.WidgetMonitorX = cursor.X;
        s.WidgetMonitorY = cursor.Y;
        s.Save();

        ApplyEdge(edge);
        _lastNear = DateTime.Now;
        Dispatcher.BeginInvoke(Place, DispatcherPriority.Loaded);
    }

    private static bool IsInside<T>(object source) where T : DependencyObject
    {
        for (var d = source as DependencyObject; d is not null; d = VisualTreeHelper.GetParent(d))
            if (d is T) return true;
        return false;
    }

    private static bool IsWithin(object source, DependencyObject ancestor)
    {
        for (var d = source as DependencyObject; d is not null; d = VisualTreeHelper.GetParent(d))
            if (d == ancestor) return true;
        return false;
    }

    // ------------------------------------------------------------------ profiles & buttons

    private void SetProfilesOpen(bool open)
    {
        ProfilesPanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        StripIcon.Text = open ? "✕" : "☰";
        _lastNear = DateTime.Now;
    }

    private void Profiles_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || Profiles.SelectedItem is not ProfileRow row) return;
        _app.Settings.WidgetProfile = row.Profile;
        _app.Settings.Save();
        RefreshProfiles();
    }

    private void Profiles_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Profiles.SelectedItem is ProfileRow row) _app.StartCapture(row.Profile);
    }

    private void Capture_Click(object sender, RoutedEventArgs e) => _app.StartCapture(_app.Settings.WidgetProfile);
    private void Editor_Click(object sender, RoutedEventArgs e) => _app.OpenLatestInEditor();
    private void Settings_Click(object sender, RoutedEventArgs e) => _app.OpenSettings();

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        _app.Settings.Save();
        base.OnClosed(e);
    }
}
