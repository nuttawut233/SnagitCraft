using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using static SnagitAnything.Native.NativeMethods;

namespace SnagitAnything.Capture;

/// <summary>
/// Full-screen picker shown on ONE monitor. It draws the frozen snapshot, so what you select is
/// exactly what gets saved. The window is opaque (no AllowsTransparency): layered full-screen
/// windows are the expensive path that pushed Snagit 12's renderer out of memory on wide screens.
/// </summary>
internal sealed class CaptureOverlayWindow : Window
{
    private const double DragThreshold = 4;
    private const int MagnifierSourcePx = 21;
    private const double MagnifierSize = 126;

    private readonly ScreenSnapshot _snapshot;
    private readonly MonitorInfo _monitor;

    private readonly Path _dim = new() { Fill = new SolidColorBrush(Color.FromArgb(0x88, 0, 0, 0)), IsHitTestVisible = false };
    private readonly Rectangle _frame = new()
    {
        Stroke = new SolidColorBrush(Color.FromRgb(0xFF, 0x8A, 0x00)), StrokeThickness = 2,
        IsHitTestVisible = false, Visibility = Visibility.Collapsed,
    };
    private readonly Line _crossH = MakeCrossLine();
    private readonly Line _crossV = MakeCrossLine();
    private readonly Border _sizeLabel;
    private readonly TextBlock _sizeText = new() { Foreground = Brushes.White, FontSize = 12, FontFamily = new FontFamily("Segoe UI") };
    private readonly Border _magnifier;
    private readonly ImageBrush _magnifierBrush;
    private readonly Canvas _canvas = new();

    private Point? _dragStart;
    private bool _dragging;

    /// <summary>Raised with the chosen rectangle in absolute physical pixels, or null when cancelled.</summary>
    public event Action<Int32Rect?>? Finished;
    public event Action? FullScreenRequested;

    public CaptureOverlayWindow(ScreenSnapshot snapshot, MonitorInfo monitor)
    {
        _snapshot = snapshot;
        _monitor = monitor;

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Background = Brushes.Black;
        Cursor = Cursors.Cross;
        WindowStartupLocation = WindowStartupLocation.Manual;
        // The real placement is done in physical pixels by SnapToMonitor. DIP sizes set here would be
        // interpreted at the primary monitor's DPI and then rescaled by WPF when the window lands on a
        // monitor with another DPI, leaving the overlay smaller than the screen it should cover.
        Left = 0;
        Top = 0;
        Width = 100;
        Height = 100;

        // Each overlay gets its own monitor-sized copy for the background AND the magnifier. Handing the
        // whole multi-monitor snapshot to every window would upload it to the GPU once per window.
        var local = new Int32Rect(monitor.Bounds.X - snapshot.VirtualBounds.X, monitor.Bounds.Y - snapshot.VirtualBounds.Y,
            monitor.Bounds.Width, monitor.Bounds.Height);
        var monitorImage = ScreenCapture.Materialize(snapshot.Image, local);

        var background = new Image { Source = monitorImage, Stretch = Stretch.Fill };
        RenderOptions.SetBitmapScalingMode(background, BitmapScalingMode.NearestNeighbor);

        _sizeLabel = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xDD, 0x20, 0x20, 0x20)),
            CornerRadius = new CornerRadius(3), Padding = new Thickness(6, 2, 6, 2),
            Child = _sizeText, IsHitTestVisible = false,
        };

        _magnifierBrush = new ImageBrush(monitorImage) { ViewboxUnits = BrushMappingMode.Absolute, Stretch = Stretch.Fill };
        var magnifierFill = new Rectangle { Width = MagnifierSize, Height = MagnifierSize, Fill = _magnifierBrush };
        RenderOptions.SetBitmapScalingMode(magnifierFill, BitmapScalingMode.NearestNeighbor);
        var magnifierGrid = new Grid();
        magnifierGrid.Children.Add(magnifierFill);
        magnifierGrid.Children.Add(new Rectangle
        {
            Width = MagnifierSize / MagnifierSourcePx, Height = MagnifierSize / MagnifierSourcePx,
            Stroke = Brushes.Red, StrokeThickness = 1,
        });
        _magnifier = new Border
        {
            BorderBrush = Brushes.White, BorderThickness = new Thickness(2), Child = magnifierGrid,
            IsHitTestVisible = false, Visibility = Visibility.Collapsed,
        };

        var hint = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xDD, 0x20, 0x20, 0x20)),
            CornerRadius = new CornerRadius(4), Padding = new Thickness(12, 6, 12, 6),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 16, 0, 0), IsHitTestVisible = false,
            Child = new TextBlock
            {
                Foreground = Brushes.White, FontSize = 13, FontFamily = new FontFamily("Segoe UI"),
                Text = "ลาก = เลือกพื้นที่   •   คลิก = จับหน้าต่าง   •   F = เต็มจอ   •   Esc / คลิกขวา = ยกเลิก",
            },
        };

        _canvas.Children.Add(_dim);
        _canvas.Children.Add(_crossH);
        _canvas.Children.Add(_crossV);
        _canvas.Children.Add(_frame);
        _canvas.Children.Add(_sizeLabel);
        _canvas.Children.Add(_magnifier);

        var root = new Grid();
        root.Children.Add(background);
        root.Children.Add(_canvas);
        root.Children.Add(hint);
        Content = root;

        Loaded += (_, _) => UpdateHover(Mouse.GetPosition(this));
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        SnapToMonitor();
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        // WPF applies its own DPI-scaled size after this callback; re-snap once it has.
        Dispatcher.BeginInvoke(SnapToMonitor, System.Windows.Threading.DispatcherPriority.Send);
    }

    /// <summary>Covers the monitor exactly, in physical pixels, whatever DPI the window started with.</summary>
    private void SnapToMonitor()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        var b = _monitor.Bounds;
        SetWindowPos(hwnd, HWND_TOPMOST, b.X, b.Y, b.Width, b.Height, SWP_SHOWWINDOW);
        GetWindowRect(hwnd, out var r);
        if (r.Left != b.X || r.Top != b.Y || r.Width != b.Width || r.Height != b.Height)
            SetWindowPos(hwnd, HWND_TOPMOST, b.X, b.Y, b.Width, b.Height, SWP_SHOWWINDOW);
    }

    /// <summary>True when the window really covers its monitor (used by the self-test).</summary>
    public bool CoversMonitor()
    {
        GetWindowRect(new WindowInteropHelper(this).Handle, out var r);
        var b = _monitor.Bounds;
        return r.Left == b.X && r.Top == b.Y && r.Width == b.Width && r.Height == b.Height;
    }

    public MonitorInfo Monitor => _monitor;

    /// <summary>Device scale from Windows itself; WPF's GetDpi can lag behind after a cross-monitor move.</summary>
    private double Scale
    {
        get
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            uint dpi = hwnd == IntPtr.Zero ? 0 : GetDpiForWindow(hwnd);
            return dpi > 0 ? dpi / 96.0 : VisualTreeHelper.GetDpi(this).DpiScaleX;
        }
    }

    private Point ToPhysical(Point local) => new(_monitor.Bounds.X + local.X * Scale, _monitor.Bounds.Y + local.Y * Scale);

    private Rect ToLocal(Int32Rect physical)
    {
        double s = Scale;
        return new Rect((physical.X - _monitor.Bounds.X) / s, (physical.Y - _monitor.Bounds.Y) / s,
            physical.Width / s, physical.Height / s);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(this);
        _dragging = false;
        CaptureMouse();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        if (_dragStart is { } start && e.LeftButton == MouseButtonState.Pressed)
        {
            if (!_dragging && (Math.Abs(p.X - start.X) > DragThreshold || Math.Abs(p.Y - start.Y) > DragThreshold))
                _dragging = true;
            if (_dragging)
            {
                ShowSelection(PhysicalRect(start, p), p);
                return;
            }
        }
        UpdateHover(p);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        ReleaseMouseCapture();
        if (_dragStart is not { } start) return;
        var p = e.GetPosition(this);
        _dragStart = null;

        if (_dragging)
        {
            var r = PhysicalRect(start, p);
            if (r.Width >= 2 && r.Height >= 2) Finished?.Invoke(r);
            return;
        }

        // Plain click: the window under the cursor, or the whole monitor if there is none.
        var hit = WindowAt(ToPhysical(p));
        Finished?.Invoke(hit?.Bounds ?? _monitor.Bounds);
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e) => Finished?.Invoke(null);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape: Finished?.Invoke(null); break;
            case Key.F: FullScreenRequested?.Invoke(); break;
            case Key.M: Finished?.Invoke(_monitor.Bounds); break;
        }
    }

    private Int32Rect PhysicalRect(Point a, Point b)
    {
        var pa = ToPhysical(a);
        var pb = ToPhysical(b);
        int x1 = (int)Math.Round(Math.Min(pa.X, pb.X)), y1 = (int)Math.Round(Math.Min(pa.Y, pb.Y));
        int x2 = (int)Math.Round(Math.Max(pa.X, pb.X)), y2 = (int)Math.Round(Math.Max(pa.Y, pb.Y));
        return new Int32Rect(x1, y1, Math.Max(1, x2 - x1), Math.Max(1, y2 - y1));
    }

    private WindowInfo? WindowAt(Point physical) =>
        _snapshot.Windows.FirstOrDefault(w =>
            physical.X >= w.Bounds.X && physical.X < w.Bounds.X + w.Bounds.Width &&
            physical.Y >= w.Bounds.Y && physical.Y < w.Bounds.Y + w.Bounds.Height);

    private void UpdateHover(Point p)
    {
        var hit = WindowAt(ToPhysical(p));
        if (hit is null)
        {
            _frame.Visibility = Visibility.Collapsed;
            _dim.Data = new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight));
            _sizeLabel.Visibility = Visibility.Collapsed;
        }
        else
        {
            ShowSelection(hit.Bounds, p);
        }
        UpdateCrosshairAndMagnifier(p);
    }

    private void ShowSelection(Int32Rect physical, Point cursor)
    {
        var local = ToLocal(physical);
        var full = new Rect(0, 0, ActualWidth, ActualHeight);
        _dim.Data = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(full), new RectangleGeometry(local));

        _frame.Visibility = Visibility.Visible;
        Canvas.SetLeft(_frame, local.X);
        Canvas.SetTop(_frame, local.Y);
        _frame.Width = Math.Max(0, local.Width);
        _frame.Height = Math.Max(0, local.Height);

        _sizeText.Text = $"{physical.Width} × {physical.Height}";
        _sizeLabel.Visibility = Visibility.Visible;
        double labelY = local.Y - 26 < 0 ? local.Y + 6 : local.Y - 26;
        Canvas.SetLeft(_sizeLabel, Math.Max(0, local.X));
        Canvas.SetTop(_sizeLabel, Math.Max(0, labelY));

        UpdateCrosshairAndMagnifier(cursor);
    }

    private void UpdateCrosshairAndMagnifier(Point p)
    {
        _crossH.X1 = 0; _crossH.X2 = ActualWidth; _crossH.Y1 = _crossH.Y2 = p.Y;
        _crossV.Y1 = 0; _crossV.Y2 = ActualHeight; _crossV.X1 = _crossV.X2 = p.X;

        var phys = ToPhysical(p);
        double sx = phys.X - _monitor.Bounds.X - MagnifierSourcePx / 2.0;
        double sy = phys.Y - _monitor.Bounds.Y - MagnifierSourcePx / 2.0;
        _magnifierBrush.Viewbox = new Rect(sx, sy, MagnifierSourcePx, MagnifierSourcePx);

        double mx = p.X + 24, my = p.Y + 24;
        if (mx + MagnifierSize + 8 > ActualWidth) mx = p.X - MagnifierSize - 28;
        if (my + MagnifierSize + 8 > ActualHeight) my = p.Y - MagnifierSize - 28;
        Canvas.SetLeft(_magnifier, mx);
        Canvas.SetTop(_magnifier, my);
        _magnifier.Visibility = Visibility.Visible;
    }

    private static Line MakeCrossLine() => new()
    {
        Stroke = new SolidColorBrush(Color.FromArgb(0xAA, 0xFF, 0xFF, 0xFF)), StrokeThickness = 1,
        StrokeDashArray = new DoubleCollection { 4, 4 }, IsHitTestVisible = false,
    };
}
