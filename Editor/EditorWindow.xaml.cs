using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Microsoft.Win32;
using SnagitAnything.Capture;
using SnagitAnything.Services;

namespace SnagitAnything.Editor;

/// <summary>
/// Single editor window (like Snagit Editor): ribbon on top, canvas in the middle, recent captures
/// tray at the bottom. Captures and opened files load into this one window.
/// </summary>
public partial class EditorWindow : Window
{
    private enum DragMode { None, Draw, Move, HandleA, HandleB, Region, Canvas }

    [Flags]
    private enum CanvasEdge { Left = 1, Top = 2, Right = 4, Bottom = 8 }

    private CanvasEdge _canvasEdges;
    private readonly List<Rectangle> _canvasHandles = new();
    private bool _allSelected;

    private static readonly double[] FontSizes = { 10, 12, 14, 16, 18, 20, 24, 28, 32, 40, 48, 64, 72 };

    private readonly App _app;
    private readonly UndoStack _history = new();
    private readonly Dictionary<Tool, AnnotationStyle> _styles = new();
    private BitmapSource _base = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[4], 4);
    private string? _filePath;

    private Tool _tool = Tool.Select;
    private int _nextStep = 1;
    private bool _syncingUi;

    // Mouse interaction
    private DragMode _drag;
    private Point _start;
    private Annotation? _draft;
    private FrameworkElement? _draftElement;
    private FrameworkElement? _selected;
    private Annotation? _before;

    // In-place text editing
    private TextBox? _editor;
    private FrameworkElement? _editingElement;
    private Annotation? _editingNew;

    public event Action<string>? Saved;

    public EditorWindow(App app)
    {
        _app = app;
        InitializeComponent();

        foreach (Tool t in Enum.GetValues<Tool>())
        {
            var presets = Annotations.Presets(t);
            if (presets.Count > 0) _styles[t] = presets[0];
        }

        foreach (var rb in ToolGrid.Children.OfType<RadioButton>())
            rb.Checked += (_, _) => SetTool(Enum.Parse<Tool>((string)rb.Tag));

        _history.Changed += UpdateChrome;
        Scroller.PreviewMouseWheel += Scroller_PreviewMouseWheel;
        PreviewKeyDown += Window_PreviewKeyDown;
        StateChanged += (_, _) => UpdateMaximizedLayout();
        SizeChanged += (_, _) => { if (_base.PixelWidth > 1) UpdateZoomText(); };

        CreateCanvasHandles();
        ImageBackdrop.Background = CheckerBrush(16); // shows through where the canvas is transparent
        SetTool(Tool.Select);
        UpdateChrome();
    }

    // ================================================================== loading

    /// <summary>Shows a capture or file. Returns false if the user cancelled because of unsaved changes.</summary>
    public bool LoadCapture(BitmapSource image, string? path)
    {
        if (!ConfirmDiscard()) return false;
        CommitText();
        Select(null);
        _history.Clear();
        _history.IsDirty = false;
        _nextStep = 1;
        _filePath = path;
        ResetContent(image);
        RefreshTray();
        if (IsLoaded) FitInitial();
        else Loaded += (_, _) => FitInitial();
        UpdateChrome();
        StatusText.Text = path ?? "ภาพใหม่";
        return true;
    }

    /// <summary>
    /// Shows <paramref name="picture"/> as its own object on a transparent canvas of the same size.
    /// The picture can then be scaled / moved without touching the canvas, and the canvas handles
    /// resize the background without scaling the picture.
    /// </summary>
    private void ResetContent(BitmapSource picture)
    {
        Select(null);
        AnnotationLayer.Children.Clear();
        int w = picture.PixelWidth, h = picture.PixelHeight;
        SetBase(ImageOps.Blank(w, h));
        AnnotationLayer.Children.Add(Annotations.Build(new Annotation
        {
            Kind = Tool.Picture, A = new Point(0, 0), B = new Point(w, h), Pixels = picture,
            Style = new AnnotationStyle(Colors.Transparent, Colors.Transparent, 0, false, 0),
        }));
    }

    private void SetBase(BitmapSource image)
    {
        _base = image;
        BaseImage.Source = image;
        ImageHost.Width = InputLayer.Width = ImageBackdrop.Width = image.PixelWidth;
        ImageHost.Height = InputLayer.Height = ImageBackdrop.Height = image.PixelHeight;
        PositionCanvasHandles();
        UpdateChrome();
    }

    private bool ConfirmDiscard()
    {
        CommitText();
        if (!_history.IsDirty) return true;
        var answer = MessageBox.Show(this, "บันทึกการแก้ไขภาพนี้ก่อนหรือไม่?", "SnagitCraft",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        return answer switch
        {
            MessageBoxResult.Yes => Save(),
            MessageBoxResult.No => true,
            _ => false,
        };
    }

    // ================================================================== tray

    public void RefreshTray()
    {
        var items = _app.Library.Recent(40).Select(f =>
        {
            BitmapSource? thumb = null;
            try { thumb = CaptureLibrary.Load(f.FullName, decodeWidth: 220); }
            catch (Exception ex) { Log.Error("Tray thumbnail failed", ex); }
            return new RecentItem(f.FullName, thumb, $"{f.Name}\n{f.LastWriteTime:dd/MM/yyyy HH:mm:ss}");
        }).ToList();

        _syncingUi = true;
        Tray.ItemsSource = items;
        Tray.SelectedItem = items.FirstOrDefault(i => string.Equals(i.Path, _filePath, StringComparison.OrdinalIgnoreCase));
        _syncingUi = false;
    }

    private void Tray_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingUi || Tray.SelectedItem is not RecentItem item) return;
        if (string.Equals(item.Path, _filePath, StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            if (!LoadCapture(CaptureLibrary.Load(item.Path), item.Path)) RefreshTray(); // cancelled: restore selection
        }
        catch (Exception ex)
        {
            Log.Error("Open from tray failed", ex);
            StatusText.Text = "เปิดไฟล์ไม่ได้: " + ex.Message;
        }
    }

    private void TrayToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (TrayPanel is null) return; // raised while InitializeComponent is still building the window
        TrayPanel.Visibility = TrayToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RefreshTray_Click(object sender, RoutedEventArgs e) => RefreshTray();

    // ================================================================== window chrome

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        PlaceOnCursorMonitor();
    }

    /// <summary>
    /// Opens on the monitor under the mouse, centred in its work area and never larger than it.
    /// WPF's CenterScreen works in DIPs of the primary monitor, which puts the window partly off-screen
    /// (status bar behind the taskbar) on secondary monitors with a different scale.
    /// </summary>
    private void PlaceOnCursorMonitor()
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        Native.NativeMethods.GetCursorPos(out var cursor);
        var work = Native.NativeMethods.GetWorkAreaAt(cursor);
        if (work.Width <= 0) return;
        double scale = Native.NativeMethods.GetScaleAt(cursor);
        int w = (int)Math.Min(Width * scale, work.Width * 0.92);
        int h = (int)Math.Min(Height * scale, work.Height * 0.92);
        int x = work.Left + (work.Width - w) / 2;
        int y = work.Top + (work.Height - h) / 2;
        const uint flags = Native.NativeMethods.SWP_NOZORDER | Native.NativeMethods.SWP_NOACTIVATE;
        // Move first: crossing onto a monitor with another DPI makes WPF rescale the window right away.
        // Sizing afterwards keeps that rescale from being applied on top of our physical size.
        Native.NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, flags | 0x0001 /* SWP_NOSIZE */);
        Native.NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, x, y, w, h, flags);
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);

    private void Maximize_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void UpdateMaximizedLayout()
    {
        // A maximized WindowChrome window extends past the screen by the resize border; pad it back in.
        var b = SystemParameters.WindowResizeBorderThickness;
        RootBorder.Margin = WindowState == WindowState.Maximized
            ? new Thickness(b.Left + 2, b.Top + 2, b.Right + 2, b.Bottom + 2)
            : new Thickness(0);
        MaxIcon.Data = Geometry.Parse(WindowState == WindowState.Maximized
            ? "M7.5,5.5 H14.5 V12.5 M5.5,7.5 H12.5 V14.5 H5.5 Z"
            : "M5.5,5.5 H14.5 V14.5 H5.5 Z");
    }

    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (PanelTools is null) return; // during InitializeComponent
        PanelTools.Visibility = TabTools.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PanelImage.Visibility = TabImage.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PanelShare.Visibility = TabShare.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PanelLibrary.Visibility = TabLibrary.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void File_Click(object sender, RoutedEventArgs e)
    {
        FileMenu.PlacementTarget = FileButton;
        FileMenu.Placement = PlacementMode.Bottom;
        FileMenu.IsOpen = true;
    }

    // ================================================================== zoom

    /// <summary>
    /// Current device scale of this window, asked from Windows. WPF's own GetDpi / TransformToDevice keep
    /// reporting the primary monitor's scale when the window is moved to another monitor before it is shown
    /// (seen: 1.25 reported while the window really renders at 2.0 on a 200% 4K screen).
    /// </summary>
    private double DpiScale
    {
        get
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            double scale = hwnd == IntPtr.Zero ? 0 : Native.NativeMethods.GetScaleOfWindowMonitor(hwnd);
            return scale > 0 ? scale : VisualTreeHelper.GetDpi(this).DpiScaleX;
        }
    }

    private void FitInitial()
    {
        _zoomTouched = false;
        SetZoom(Math.Min(1 / DpiScale, FitScale()));
    }

    /// <summary>True once the user zoomed by hand; until then a DPI change simply refits the picture.</summary>
    private bool _zoomTouched;

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        // Moving to a monitor with another scale (this also happens right after opening, when the editor
        // lands on the monitor under the mouse): keep the picture fitted, or keep the user's zoom % the same.
        double ratio = oldDpi.DpiScaleX / newDpi.DpiScaleX;
        Dispatcher.BeginInvoke(() =>
        {
            if (_base.PixelWidth <= 1) return;
            if (_zoomTouched) SetZoom(Zoom.ScaleX * ratio);
            else FitInitial();
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private double FitScale()
    {
        double w = Math.Max(100, Scroller.ActualWidth - 90);
        double h = Math.Max(100, Scroller.ActualHeight - 90);
        return Math.Min(w / _base.PixelWidth, h / _base.PixelHeight);
    }

    private void SetZoom(double z)
    {
        z = Math.Clamp(z, 0.05, 16);
        Zoom.ScaleX = Zoom.ScaleY = z;
        UpdateZoomText();
        PositionCanvasHandles();
        RefreshSelectionChrome();
    }

    private void UpdateZoomText()
    {
        double percent = Math.Round(Zoom.ScaleX * DpiScale * 100);        ZoomText.Text = $"{percent}%";
        _syncingUi = true;
        ZoomSlider.Value = Math.Clamp(percent, ZoomSlider.Minimum, ZoomSlider.Maximum);
        _syncingUi = false;
    }

    private void ZoomSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncingUi || !IsLoaded) return;
        _zoomTouched = true;
        SetZoom(e.NewValue / 100 / DpiScale);
    }

    private void ZoomIn_Click(object sender, RoutedEventArgs e) { _zoomTouched = true; SetZoom(Zoom.ScaleX * 1.25); }
    private void ZoomOut_Click(object sender, RoutedEventArgs e) { _zoomTouched = true; SetZoom(Zoom.ScaleX / 1.25); }
    private void ZoomFit_Click(object sender, RoutedEventArgs e) { _zoomTouched = true; SetZoom(FitScale()); }
    private void ZoomActual_Click(object sender, RoutedEventArgs e) { _zoomTouched = true; SetZoom(1 / DpiScale); }

    private void Scroller_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        _zoomTouched = true;
        SetZoom(e.Delta > 0 ? Zoom.ScaleX * 1.15 : Zoom.ScaleX / 1.15);
        e.Handled = true;
    }

    // ================================================================== chrome / status

    private void UpdateChrome()
    {
        QatUndo.IsEnabled = _history.CanUndo;
        QatRedo.IsEnabled = _history.CanRedo;
        string name = _filePath is null ? "ภาพใหม่" : System.IO.Path.GetFileName(_filePath);
        Title = $"{name} — SnagitCraft Editor";
        TitleText.Text = $"{(_history.IsDirty ? "● " : "")}SnagitCraft Editor — [{name}]";
        SizeText.Text = $"{_base.PixelWidth} × {_base.PixelHeight}";
        StepInfo.Text = $"ลำดับถัดไป: {_nextStep}";
    }

    // ================================================================== tools & styles

    private void SetTool(Tool tool)
    {
        CommitText();
        _tool = tool;
        if (tool != Tool.Select) Select(null);
        InputLayer.Cursor = tool switch
        {
            Tool.Select => Cursors.Arrow,
            Tool.Text => Cursors.IBeam,
            _ => Cursors.Cross,
        };
        var rb = ToolGrid.Children.OfType<RadioButton>().FirstOrDefault(r => (string)r.Tag == tool.ToString());
        if (rb is { IsChecked: false }) rb.IsChecked = true;
        SyncStyleUi();
    }

    /// <summary>The tool whose style the ribbon shows: the selected object's kind, else the active tool.</summary>
    private Tool StyleTool => _selected?.Tag is Annotation a ? a.Kind : _tool;

    private AnnotationStyle? CurrentStyle => _selected?.Tag is Annotation a ? a.Style
        : _styles.TryGetValue(_tool, out var s) ? s : null;

    private void SyncStyleUi()
    {
        _syncingUi = true;
        var tool = StyleTool;
        var style = CurrentStyle;
        var presets = Annotations.Presets(tool);

        Gallery.Items.Clear();
        foreach (var p in presets)
            Gallery.Items.Add(new ListBoxItem { Content = Annotations.Preview(tool, p), Tag = p, ToolTip = Describe(p) });
        Gallery.SelectedItem = Gallery.Items.OfType<ListBoxItem>().FirstOrDefault(i => Equals(i.Tag, style));

        bool styled = style is not null && tool is not Tool.Blur and not Tool.Crop and not Tool.Picture;
        OutlineButton.IsEnabled = EffectsButton.IsEnabled = styled;
        GalleryHint.Visibility = presets.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        FillButton.IsEnabled = styled && tool is Tool.Rectangle or Tool.Ellipse or Tool.Callout or Tool.Step;
        FontSizeGroup.IsEnabled = styled && tool is Tool.Text or Tool.Callout or Tool.Step;
        FontSizeGroup.Opacity = FontSizeGroup.IsEnabled ? 1 : 0.4;
        OutlineSwatch.Background = style is null ? Brushes.Transparent : new SolidColorBrush(style.Outline);
        FillSwatch.Background = style is null || style.Fill.A == 0 ? CheckerBrush() : new SolidColorBrush(style.Fill);
        FontSizeBox.Text = FontSizeGroup.IsEnabled ? style?.FontSize.ToString() ?? "" : "";
        _syncingUi = false;
    }

    private static string Describe(AnnotationStyle s) =>
        $"เส้น {(s.Outline.A == 0 ? "ไม่มี" : s.Outline.ToString())}, ความหนา {s.Width}" +
        (s.Fill.A > 0 ? $", พื้น {s.Fill}" : "") + (s.Shadow ? ", มีเงา" : "");

    private static Brush CheckerBrush(double size = 8)
    {
        double half = size / 2;
        var g = new DrawingGroup();
        g.Children.Add(new GeometryDrawing(Brushes.White, null, new RectangleGeometry(new Rect(0, 0, size, size))));
        g.Children.Add(new GeometryDrawing(Brushes.Gainsboro, null, new RectangleGeometry(new Rect(0, 0, half, half))));
        g.Children.Add(new GeometryDrawing(Brushes.Gainsboro, null, new RectangleGeometry(new Rect(half, half, half, half))));
        return new DrawingBrush(g) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, size, size), ViewportUnits = BrushMappingMode.Absolute };
    }

    /// <summary>Changes the style of the selected object (undoable) and remembers it for the tool.</summary>
    private void ChangeStyle(Func<AnnotationStyle, AnnotationStyle> change)
    {
        var tool = StyleTool;
        if (CurrentStyle is not { } current) return;
        var next = change(current);
        _styles[tool] = next;
        if (_selected?.Tag is Annotation a)
        {
            var updated = a.Clone();
            updated.Style = next;
            // For a step badge the font size is its size: resize the badge around its centre.
            if (a.Kind == Tool.Step && Math.Abs(next.FontSize - current.FontSize) > 0.01)
            {
                var box = Annotations.Normalize(a.A, a.B);
                var center = new Point(box.X + box.Width / 2, box.Y + box.Height / 2);
                (updated.A, updated.B) = Annotations.StepBox(center, Annotations.StepSize(next.FontSize));
            }
            ReplaceSelected(updated, record: true);
        }
        SyncStyleUi();
    }

    private void Gallery_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingUi || Gallery.SelectedItem is not ListBoxItem { Tag: AnnotationStyle preset }) return;
        // Keep the user's font size when switching presets on text-like objects.
        ChangeStyle(cur => preset with { FontSize = StyleTool is Tool.Text or Tool.Callout or Tool.Step ? cur.FontSize : preset.FontSize });
    }

    /// <summary>The gallery has no visible scrollbar; the mouse wheel scrolls it sideways.</summary>
    private void Gallery_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (VisualTreeHelper.GetChildrenCount(Gallery) == 0) return;
        var sv = FindChild<ScrollViewer>(Gallery);
        if (sv is null) return;
        sv.ScrollToHorizontalOffset(sv.HorizontalOffset - e.Delta / 2.0);
        e.Handled = true;
    }

    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T found) return found;
            if (FindChild<T>(child) is { } deeper) return deeper;
        }
        return null;
    }

    private void FontSize_Changed(object sender, RoutedEventArgs e) => ApplyFontSize(FontSizeBox.Text);

    private void FontSize_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        ApplyFontSize(FontSizeBox.Text);
        e.Handled = true;
    }

    private void ApplyFontSize(string text)
    {
        if (_syncingUi || !FontSizeGroup.IsEnabled) return;
        if (!double.TryParse(text, out double size) || size < 4 || size > 400) return;
        if (CurrentStyle is { } s && Math.Abs(s.FontSize - size) > 0.01) ChangeStyle(cur => cur with { FontSize = size });
    }

    /// <summary>Steps through the standard sizes (10, 12, … 72) from the current one.</summary>
    private void StepFontSize(int direction)
    {
        if (CurrentStyle is not { } s) return;
        double next = direction > 0
            ? FontSizes.FirstOrDefault(f => f > s.FontSize + 0.01, s.FontSize + 8)
            : FontSizes.LastOrDefault(f => f < s.FontSize - 0.01, Math.Max(4, s.FontSize - 2));
        ApplyFontSize(next.ToString());
    }

    private void FontSmaller_Click(object sender, RoutedEventArgs e) => StepFontSize(-1);
    private void FontBigger_Click(object sender, RoutedEventArgs e) => StepFontSize(+1);

    private void Outline_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentStyle is not { } style) return;
        var panel = new StackPanel { Margin = new Thickness(8) };
        panel.Children.Add(PopupLabel("สีเส้นขอบ"));
        panel.Children.Add(ColorGrid(c => ChangeStyle(s => s with { Outline = c }), allowNone: StyleTool is Tool.Rectangle or Tool.Ellipse));
        if (StyleTool is not Tool.Highlight and not Tool.Text)
        {
            panel.Children.Add(PopupLabel("ความหนา"));
            var slider = new Slider { Minimum = 0, Maximum = 24, Value = style.Width, Width = 200, IsSnapToTickEnabled = true, TickFrequency = 1 };
            var value = new TextBlock { Foreground = Brushes.White, Margin = new Thickness(8, 0, 0, 0), Text = style.Width.ToString(), Width = 24 };
            slider.ValueChanged += (_, args) => value.Text = args.NewValue.ToString();
            slider.PreviewMouseUp += (_, _) => ChangeStyle(s => s with { Width = slider.Value });
            slider.LostKeyboardFocus += (_, _) => { if (CurrentStyle?.Width != slider.Value) ChangeStyle(s => s with { Width = slider.Value }); };
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(slider);
            row.Children.Add(value);
            panel.Children.Add(row);
        }
        ShowPopup(OutlineButton, panel);
    }

    private void Fill_Click(object sender, RoutedEventArgs e)
    {
        var panel = new StackPanel { Margin = new Thickness(8) };
        panel.Children.Add(PopupLabel("สีพื้น"));
        panel.Children.Add(ColorGrid(c => ChangeStyle(s => s with { Fill = c }),
            allowNone: StyleTool is Tool.Rectangle or Tool.Ellipse, translucent: StyleTool is Tool.Rectangle or Tool.Ellipse));
        ShowPopup(FillButton, panel);
    }

    private void Effects_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentStyle is not { } style) return;
        var panel = new StackPanel { Margin = new Thickness(10) };
        panel.Children.Add(PopupLabel("เอฟเฟกต์"));
        var shadow = new CheckBox { Content = "เงา (Shadow)", IsChecked = style.Shadow, Foreground = Brushes.White, Margin = new Thickness(0, 4, 0, 0) };
        shadow.Click += (_, _) => ChangeStyle(s => s with { Shadow = shadow.IsChecked == true });
        panel.Children.Add(shadow);
        if (StyleTool == Tool.Step)
        {
            var square = new CheckBox { Content = "สี่เหลี่ยม (แทนวงกลม)", IsChecked = style.StepShape == StepShape.Square, Foreground = Brushes.White, Margin = new Thickness(0, 6, 0, 0) };
            square.Click += (_, _) => ChangeStyle(s => s with { StepShape = square.IsChecked == true ? StepShape.Square : StepShape.Circle });
            panel.Children.Add(square);
        }
        ShowPopup(EffectsButton, panel);
    }

    private static TextBlock PopupLabel(string text) =>
        new() { Text = text, Foreground = new SolidColorBrush(Color.FromRgb(0xB0, 0xB0, 0xB0)), Margin = new Thickness(0, 4, 0, 4), FontSize = 11 };

    private UIElement ColorGrid(Action<Color> pick, bool allowNone, bool translucent = false)
    {
        var wrap = new WrapPanel { Width = 230 };
        void Add(Color c, string? tip = null)
        {
            var b = new Button
            {
                Width = 22, Height = 22, Margin = new Thickness(2), Cursor = Cursors.Hand, ToolTip = tip ?? c.ToString(),
                Background = c.A == 0 ? CheckerBrush() : new SolidColorBrush(c), BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1),
                Template = SwatchTemplate(),
            };
            b.Click += (_, _) => { pick(c); ClosePopups(); };
            wrap.Children.Add(b);
        }
        if (allowNone) Add(Annotations.None, "ไม่มี");
        foreach (var c in Annotations.Palette) Add(c);
        if (translucent)
            foreach (var c in Annotations.Palette.Take(7)) Add(Color.FromArgb(0x44, c.R, c.G, c.B), "โปร่งใส");
        return wrap;
    }

    private static ControlTemplate SwatchTemplate()
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(BackgroundProperty));
        border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(BorderBrushProperty));
        border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(BorderThicknessProperty));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
        return new ControlTemplate(typeof(Button)) { VisualTree = border };
    }

    private Popup? _popup;

    private void ShowPopup(UIElement target, UIElement content)
    {
        ClosePopups();
        _popup = new Popup
        {
            PlacementTarget = target, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true,
            Child = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x30)), BorderBrush = new SolidColorBrush(Color.FromRgb(0x50, 0x50, 0x50)),
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Child = content,
            },
        };
        _popup.IsOpen = true;
    }

    private void ClosePopups()
    {
        if (_popup is not null) _popup.IsOpen = false;
        _popup = null;
    }

    private void RestartSequence_Click(object sender, RoutedEventArgs e)
    {
        _nextStep = 1;
        UpdateChrome();
        StatusText.Text = "ลำดับขั้นถัดไปจะเริ่มที่ 1";
    }

    // ================================================================== mouse

    private const double HandleSize = 10;

    private void Input_MouseDown(object sender, MouseButtonEventArgs e)
    {
        CommitText();
        ClosePopups();
        _start = e.GetPosition(InputLayer);
        InputLayer.Focus();
        if (_allSelected) { _allSelected = false; RefreshSelectionChrome(); }

        // Canvas handles work with every tool, like Snagit — but a selected object's own handles win
        // (a picture that fills the canvas has its corners exactly on the canvas corners).
        bool objectHandle = _tool == Tool.Select && HandleAt(_start) != DragMode.None;
        if (!objectHandle && e.OriginalSource is Rectangle { Tag: CanvasEdge edges })
        {
            _drag = DragMode.Canvas;
            _canvasEdges = edges;
            Annotations.Place(CropPreview, new Rect(0, 0, _base.PixelWidth, _base.PixelHeight));
            CropPreview.Stroke = Brushes.DeepSkyBlue;
            CropPreview.Visibility = Visibility.Visible;
            InputLayer.CaptureMouse();
            e.Handled = true;
            return;
        }

        if (_tool == Tool.Select)
        {
            if (e.ClickCount == 2 && HitTest(_start) is { Tag: Annotation { HasText: true } } textTarget)
            {
                Select(textTarget);
                BeginEdit(textTarget);
                e.Handled = true;
                return;
            }

            var handle = HandleAt(_start);            if (handle != DragMode.None && _selected?.Tag is Annotation sel)
            {
                _drag = handle;
                _before = sel.Clone();
            }
            else
            {
                Select(HitTest(_start));
                if (_selected?.Tag is Annotation hit)
                {
                    _drag = DragMode.Move;
                    _before = hit.Clone();
                }
            }
            if (_drag != DragMode.None) InputLayer.CaptureMouse();
            e.Handled = true;
            return;
        }

        var style = _styles.GetValueOrDefault(_tool);
        switch (_tool)
        {
            case Tool.Text:
                BeginNewText(_start, style!);
                break;
            case Tool.Step:
                var (topLeft, bottomRight) = Annotations.StepBox(_start, Annotations.StepSize(style!.FontSize));
                AddAnnotation(new Annotation { Kind = Tool.Step, A = topLeft, B = bottomRight, Number = _nextStep++, Style = style });
                UpdateChrome();
                break;
            case Tool.Blur:
            case Tool.Crop:
                _drag = DragMode.Region;
                CropPreview.Stroke = _tool == Tool.Crop ? Brushes.White : Brushes.DeepSkyBlue;
                Annotations.Place(CropPreview, new Rect(_start, _start));
                CropPreview.Visibility = Visibility.Visible;
                InputLayer.CaptureMouse();
                break;
            default:
                _drag = DragMode.Draw;
                _draft = new Annotation { Kind = _tool, A = _start, B = _start, Style = style!, Text = "" };
                if (_tool == Tool.Pen) _draft.Points.Add(_start);
                _draftElement = Annotations.Build(_draft);
                AnnotationLayer.Children.Add(_draftElement);
                InputLayer.CaptureMouse();
                break;
        }
        e.Handled = true;
    }

    private void Input_MouseMove(object sender, MouseEventArgs e)
    {
        var p = e.GetPosition(InputLayer);
        if (_drag == DragMode.None)
        {
            if (_tool == Tool.Select)
                InputLayer.Cursor = HandleAt(p) != DragMode.None ? Cursors.SizeAll
                    : HitTest(p) is not null ? Cursors.Hand : Cursors.Arrow;
            return;
        }
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

        switch (_drag)
        {
            case DragMode.Move when _selected?.Tag is Annotation a && _before is not null:
                a.Offset = _before.Offset + (p - _start);
                ((TranslateTransform)_selected.RenderTransform).X = a.Offset.X;
                ((TranslateTransform)_selected.RenderTransform).Y = a.Offset.Y;
                RefreshSelectionChrome();
                break;

            case DragMode.HandleA or DragMode.HandleB when _selected?.Tag is Annotation a && _before is not null:
                var moved = a.Clone();
                var other = _drag == DragMode.HandleA ? _before.B : _before.A;
                var target = (_drag == DragMode.HandleA ? _before.A : _before.B) + (p - _start);
                if (a.Kind == Tool.Picture && !shift && a.Pixels is { } px)
                    target = KeepAspect(other, target, (double)px.PixelWidth / px.PixelHeight); // Shift = free stretch
                else if (shift || a.Kind == Tool.Step)
                    target = Constrain(a.Kind, other, target); // step badges stay square
                if (_drag == DragMode.HandleA) moved.A = target; else moved.B = target;                ReplaceSelected(moved, record: false);
                break;

            case DragMode.Draw when _draft is not null && _draftElement is not null:
                if (_draft.Kind == Tool.Pen)
                {
                    _draft.Points.Add(p);
                    ((Polyline)_draftElement).Points.Add(p);
                }
                else
                {
                    _draft.B = shift ? Constrain(_draft.Kind, _draft.A, p) : p;
                    int index = AnnotationLayer.Children.IndexOf(_draftElement);
                    AnnotationLayer.Children.RemoveAt(index);
                    _draftElement = Annotations.Build(_draft);
                    AnnotationLayer.Children.Insert(index, _draftElement);
                }
                break;

            case DragMode.Region:
                Annotations.Place(CropPreview, Annotations.Normalize(_start, shift ? Constrain(Tool.Rectangle, _start, p) : p));
                break;

            case DragMode.Canvas:
                Annotations.Place(CropPreview, CanvasRect(p - _start, shift));
                StatusText.Text = shift && IsCorner(_canvasEdges)
                    ? $"ย่อ/ขยายภาพ → {CropPreview.Width:0} × {CropPreview.Height:0} px"
                    : $"ขนาดพื้นที่ภาพ → {CropPreview.Width:0} × {CropPreview.Height:0} px   (กด Shift ที่มุม = ย่อ/ขยายภาพทั้งรูป)";
                break;
        }
    }

    private void Input_MouseUp(object sender, MouseButtonEventArgs e)
    {
        var mode = _drag;
        _drag = DragMode.None;
        InputLayer.ReleaseMouseCapture();
        var p = e.GetPosition(InputLayer);
        bool tiny = (p - _start).Length < 4;

        switch (mode)
        {
            case DragMode.Move when _selected is { Tag: Annotation a } el && _before is { } before:
                var from = before.Offset;
                var to = a.Offset;
                if (from != to)
                    _history.Push(new Edit(() => SetOffset(el, from), () => SetOffset(el, to)));
                break;

            case DragMode.HandleA or DragMode.HandleB when _selected?.Tag is Annotation && _before is { } prior:
                var current = _selected;
                var original = Annotations.Build(prior);
                RecordSwap(original, current);
                break;

            case DragMode.Draw when _draft is not null && _draftElement is not null:
                AnnotationLayer.Children.Remove(_draftElement);
                var draft = _draft;
                _draft = null;
                _draftElement = null;
                bool valid = draft.Kind == Tool.Pen ? draft.Points.Count > 2 : !tiny;
                if (draft.Kind == Tool.Callout && tiny)
                {
                    // Single click places a default-sized callout.
                    draft.B = draft.A + new Vector(220, 80);
                    valid = true;
                }
                if (!valid) break;
                var added = AddAnnotation(draft);
                if (draft.Kind == Tool.Callout)
                {
                    Select(added);
                    BeginEdit(added);
                }
                break;

            case DragMode.Canvas:
                CropPreview.Visibility = Visibility.Collapsed;
                bool scale = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 && IsCorner(_canvasEdges);
                var area = CanvasRect(p - _start, scale);
                if (tiny) break;
                if (scale)
                {
                    double factor = area.Width / _base.PixelWidth;
                    ApplyImageOp($"ย่อ/ขยายภาพ {factor * 100:0}%", img => ImageOps.Resize(img, factor));
                }
                else
                {
                    ApplyCanvas(new Int32Rect((int)Math.Round(area.X), (int)Math.Round(area.Y),
                        (int)Math.Round(area.Width), (int)Math.Round(area.Height)), "ขนาดพื้นที่ภาพ");
                }
                break;

            case DragMode.Region:
                CropPreview.Visibility = Visibility.Collapsed;
                var rect = Annotations.Normalize(_start, p);
                if (rect.Width < 4 || rect.Height < 4) break;
                if (_tool == Tool.Blur)
                {
                    var px = ToPixels(rect);
                    AddAnnotation(new Annotation
                    {
                        Kind = Tool.Blur, A = new Point(px.X, px.Y), B = new Point(px.X + px.Width, px.Y + px.Height),
                        // Sample what is visible (the picture is an object now, the canvas itself is empty).
                        Pixels = Annotations.Pixelate(Flatten(), px), Style = new AnnotationStyle(Colors.Transparent, Colors.Transparent, 0, false, 0),
                    });
                }
                else
                {
                    ApplyCrop(ToPixels(rect));
                }
                break;
        }
        _before = null;
        RefreshSelectionChrome();
    }

    private void Input_RightClick(object sender, MouseButtonEventArgs e)
    {
        var hit = HitTest(e.GetPosition(InputLayer));
        if (hit is null)
        {
            e.Handled = true; // no object under the cursor: no menu
            return;
        }
        if (_tool != Tool.Select) SetTool(Tool.Select);
        Select(hit);
        MenuEditText.Visibility = hit.Tag is Annotation { HasText: true } ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Moves <paramref name="b"/> so the box from <paramref name="a"/> keeps width/height = <paramref name="ratio"/>.</summary>
    private static Point KeepAspect(Point a, Point b, double ratio)
    {
        var v = b - a;
        double w = Math.Max(Math.Abs(v.X), Math.Abs(v.Y) * ratio);
        double h = w / ratio;
        return a + new Vector(Math.Sign(v.X == 0 ? 1 : v.X) * w, Math.Sign(v.Y == 0 ? 1 : v.Y) * h);
    }

    private static bool IsCorner(CanvasEdge e) =>
        (e.HasFlag(CanvasEdge.Left) || e.HasFlag(CanvasEdge.Right)) && (e.HasFlag(CanvasEdge.Top) || e.HasFlag(CanvasEdge.Bottom));

    /// <summary>
    /// New canvas rectangle (image pixels) for a canvas-handle drag of <paramref name="d"/>.
    /// With <paramref name="keepAspect"/> (Shift on a corner) the picture is scaled instead, so the box keeps its ratio.
    /// </summary>
    private Rect CanvasRect(Vector d, bool keepAspect)
    {
        double w = _base.PixelWidth, h = _base.PixelHeight;
        double left = 0, top = 0, right = w, bottom = h;
        if (_canvasEdges.HasFlag(CanvasEdge.Left)) left = Math.Min(right - 8, d.X);
        if (_canvasEdges.HasFlag(CanvasEdge.Right)) right = Math.Max(left + 8, w + d.X);
        if (_canvasEdges.HasFlag(CanvasEdge.Top)) top = Math.Min(bottom - 8, d.Y);
        if (_canvasEdges.HasFlag(CanvasEdge.Bottom)) bottom = Math.Max(top + 8, h + d.Y);

        if (keepAspect && IsCorner(_canvasEdges))
        {
            double factor = Math.Max(8 / Math.Min(w, h), (right - left) / w);
            double nw = w * factor, nh = h * factor;
            // Anchor at the corner opposite the handle.
            left = _canvasEdges.HasFlag(CanvasEdge.Left) ? w - nw : 0;
            top = _canvasEdges.HasFlag(CanvasEdge.Top) ? h - nh : 0;
            return new Rect(left, top, nw, nh);
        }
        return new Rect(left, top, right - left, bottom - top);
    }

    private void CreateCanvasHandles()
    {
        var edges = new[]
        {
            CanvasEdge.Left | CanvasEdge.Top, CanvasEdge.Top, CanvasEdge.Right | CanvasEdge.Top,
            CanvasEdge.Left, CanvasEdge.Right,
            CanvasEdge.Left | CanvasEdge.Bottom, CanvasEdge.Bottom, CanvasEdge.Right | CanvasEdge.Bottom,
        };
        foreach (var e in edges)
        {
            var cursor = e switch
            {
                CanvasEdge.Left or CanvasEdge.Right => Cursors.SizeWE,
                CanvasEdge.Top or CanvasEdge.Bottom => Cursors.SizeNS,
                CanvasEdge.Left | CanvasEdge.Top or CanvasEdge.Right | CanvasEdge.Bottom => Cursors.SizeNWSE,
                _ => Cursors.SizeNESW,
            };
            var r = new Rectangle
            {
                Tag = e, Fill = Brushes.White, Stroke = new SolidColorBrush(Color.FromRgb(0x30, 0x30, 0x30)), Cursor = cursor,
                ToolTip = "ลาก = ขยาย/ลดพื้นที่ภาพ   •   Shift + ลากที่มุม = ย่อ/ขยายภาพทั้งรูป",
            };
            _canvasHandles.Add(r);
            InputLayer.Children.Add(r);
        }
        // Object handles draw above the canvas handles.
        Panel.SetZIndex(HandleA, 10);
        Panel.SetZIndex(HandleB, 10);
        Panel.SetZIndex(SelectionOutline, 9);
    }

    /// <summary>Keeps the canvas handles on the picture's edges at a constant on-screen size.</summary>
    private void PositionCanvasHandles()
    {
        double s = 1 / Math.Max(0.01, Zoom.ScaleX);
        double size = 9 * s, w = _base.PixelWidth, h = _base.PixelHeight;
        foreach (var r in _canvasHandles)
        {
            var e = (CanvasEdge)r.Tag;
            double x = e.HasFlag(CanvasEdge.Left) ? 0 : e.HasFlag(CanvasEdge.Right) ? w : w / 2;
            double y = e.HasFlag(CanvasEdge.Top) ? 0 : e.HasFlag(CanvasEdge.Bottom) ? h : h / 2;
            r.Width = r.Height = size;
            r.StrokeThickness = s;
            Canvas.SetLeft(r, x - size / 2);
            Canvas.SetTop(r, y - size / 2);
        }
    }

    /// <summary>Shift: lines snap to 45°, boxes become squares.</summary>
    private static Point Constrain(Tool kind, Point a, Point b)
    {
        var v = b - a;
        if (kind is Tool.Arrow or Tool.Line)
        {
            double angle = Math.Round(Math.Atan2(v.Y, v.X) / (Math.PI / 4)) * (Math.PI / 4);
            return a + new Vector(Math.Cos(angle), Math.Sin(angle)) * v.Length;
        }
        double side = Math.Max(Math.Abs(v.X), Math.Abs(v.Y));
        return a + new Vector(Math.Sign(v.X) * side, Math.Sign(v.Y) * side);
    }

    private Int32Rect ToPixels(Rect r) => ScreenCapture.Clamp(
        new Int32Rect((int)Math.Round(r.X), (int)Math.Round(r.Y), (int)Math.Round(r.Width), (int)Math.Round(r.Height)),
        _base.PixelWidth, _base.PixelHeight);

    // ================================================================== objects

    private FrameworkElement AddAnnotation(Annotation a)
    {
        var e = Annotations.Build(a);
        AnnotationLayer.Children.Add(e);
        _history.Push(new Edit(
            () => { if (_selected == e) Select(null); AnnotationLayer.Children.Remove(e); },
            () => AnnotationLayer.Children.Add(e)));
        UpdateChrome();
        return e;
    }

    /// <summary>Rebuilds the selected object from new data, keeping its place in the z-order.</summary>
    private void ReplaceSelected(Annotation updated, bool record)
    {
        if (_selected is null) return;
        var old = _selected;
        var fresh = Annotations.Build(updated);
        Swap(old, fresh);
        if (record) RecordSwap(old, fresh);
    }

    private void Swap(FrameworkElement remove, FrameworkElement insert)
    {
        int index = AnnotationLayer.Children.IndexOf(remove);
        if (index < 0) return;
        AnnotationLayer.Children.RemoveAt(index);
        AnnotationLayer.Children.Insert(index, insert);
        if (_selected == remove) Select(insert);
    }

    /// <summary>Records that <paramref name="before"/> became <paramref name="after"/> (already applied).</summary>
    private void RecordSwap(FrameworkElement before, FrameworkElement after) =>
        _history.Push(new Edit(() => Swap(after, before), () => Swap(before, after)));

    private void SetOffset(FrameworkElement el, Vector offset)
    {
        if (el.Tag is not Annotation a) return;
        a.Offset = offset;
        var t = (TranslateTransform)el.RenderTransform;
        t.X = offset.X;
        t.Y = offset.Y;
        RefreshSelectionChrome();
    }

    private FrameworkElement? HitTest(Point p)
    {
        for (int i = AnnotationLayer.Children.Count - 1; i >= 0; i--)
        {
            if (AnnotationLayer.Children[i] is FrameworkElement fe && BoundsOf(fe) is { } b)
            {
                b.Inflate(6 / Zoom.ScaleX, 6 / Zoom.ScaleX);
                if (b.Contains(p)) return fe;
            }
        }
        return null;
    }

    private Rect? BoundsOf(FrameworkElement fe)
    {
        var bounds = VisualTreeHelper.GetDescendantBounds(fe);
        if (bounds.IsEmpty) return null;
        return fe.TransformToAncestor(AnnotationLayer).TransformBounds(bounds);
    }

    private void Select(FrameworkElement? e)
    {
        if (e is not null) _allSelected = false; // picking one object ends Ctrl+A
        bool changed = _selected != e;
        _selected = e;
        RefreshSelectionChrome();
        if (changed) SyncStyleUi();
    }

    private void RefreshSelectionChrome()
    {
        double s = 1 / Math.Max(0.01, Zoom.ScaleX);
        HandleA.Visibility = HandleB.Visibility = Visibility.Collapsed;
        if (_allSelected)
        {
            // Ctrl+A: the whole picture with everything on it.
            var all = new Rect(-3 * s, -3 * s, _base.PixelWidth + 6 * s, _base.PixelHeight + 6 * s);
            Annotations.Place(SelectionOutline, all);
            SelectionOutline.StrokeThickness = 2 * s;
            SelectionOutline.StrokeDashArray = new DoubleCollection { 4, 3 };
            SelectionOutline.Visibility = Visibility.Visible;
            return;
        }
        if (_selected is null || BoundsOf(_selected) is not { } b)
        {
            SelectionOutline.Visibility = Visibility.Collapsed;
            return;
        }
        b.Inflate(4 * s, 4 * s);
        Annotations.Place(SelectionOutline, b);
        SelectionOutline.StrokeThickness = 1.5 * s;
        SelectionOutline.StrokeDashArray = new DoubleCollection { 4, 3 };
        SelectionOutline.Visibility = Visibility.Visible;

        if (_selected.Tag is Annotation { HasHandles: true } a)
        {
            PlaceHandle(HandleA, a.A + a.Offset, s);
            PlaceHandle(HandleB, a.B + a.Offset, s);
        }
    }

    private static void PlaceHandle(Rectangle handle, Point p, double s)
    {
        handle.Width = handle.Height = HandleSize * s;
        handle.StrokeThickness = 1.5 * s;
        Canvas.SetLeft(handle, p.X - HandleSize * s / 2);
        Canvas.SetTop(handle, p.Y - HandleSize * s / 2);
        handle.Visibility = Visibility.Visible;
    }

    private DragMode HandleAt(Point p)
    {
        if (_selected?.Tag is not Annotation { HasHandles: true } a) return DragMode.None;
        double r = HandleSize / Zoom.ScaleX;
        if ((p - (a.A + a.Offset)).Length <= r) return DragMode.HandleA;
        if ((p - (a.B + a.Offset)).Length <= r) return DragMode.HandleB;
        return DragMode.None;
    }

    private void SelectAll()
    {
        CommitText();
        SetTool(Tool.Select);
        Select(null);
        _allSelected = true;
        RefreshSelectionChrome();
        StatusText.Text = $"เลือกทั้งหมด ({_base.PixelWidth} × {_base.PixelHeight} px, วัตถุ {AnnotationLayer.Children.Count} ชิ้น) — Ctrl+C คัดลอกไปวางที่โปรแกรมอื่น, Delete ลบวัตถุทั้งหมด";
    }

    /// <summary>Delete with Ctrl+A: removes every object (the picture itself stays), undoable.</summary>
    private void DeleteAllObjects()
    {
        var items = AnnotationLayer.Children.OfType<UIElement>().ToList();
        _allSelected = false;
        RefreshSelectionChrome();
        if (items.Count == 0) return;
        void Remove() { Select(null); AnnotationLayer.Children.Clear(); }
        void Restore() { foreach (var i in items) AnnotationLayer.Children.Add(i); }
        Remove();
        _history.Push(new Edit(Restore, Remove));
        UpdateChrome();
    }

    /// <summary>
    /// Ctrl+V: an image on the clipboard becomes a movable, resizable picture object (scaled to fit);
    /// text becomes a text object.
    /// </summary>
    private void Paste()
    {
        CommitText();
        if (ClipboardHelper.GetImage() is { } img)
        {
            // Pasted pictures keep their real size. If one is bigger than the canvas, the canvas grows
            // (right / down) to fit it — one undo step removes both.
            int w = img.PixelWidth, h = img.PixelHeight;
            int cw = Math.Max(_base.PixelWidth, w), ch = Math.Max(_base.PixelHeight, h);
            bool grow = cw > _base.PixelWidth || ch > _base.PixelHeight;
            SetTool(Tool.Select);
            _history.BeginGroup();
            if (grow) ApplyCanvas(new Int32Rect(0, 0, cw, ch), "ขยายพื้นที่ตามรูปที่วาง");
            var topLeft = new Point(Math.Floor((cw - w) / 2.0), Math.Floor((ch - h) / 2.0));
            var added = AddAnnotation(new Annotation
            {
                Kind = Tool.Picture, A = topLeft, B = topLeft + new Vector(w, h), Pixels = img,
                Style = new AnnotationStyle(Colors.Transparent, Colors.Transparent, 0, false, 0),
            });
            _history.EndGroup();
            Select(added);
            StatusText.Text = $"วางรูป {w} × {h} px" + (grow ? $" — ขยายพื้นที่ภาพเป็น {cw} × {ch} px" : "") +
                              "  •  ลากเพื่อย้าย, ลากมุมเพื่อย่อ/ขยาย (Shift = ไม่ล็อกสัดส่วน)";
            if (grow) Dispatcher.BeginInvoke(() => { if (!_zoomTouched) FitInitial(); });
            return;
        }
        try
        {
            if (Clipboard.ContainsText() && Clipboard.GetText() is { Length: > 0 } text)
            {
                SetTool(Tool.Select);
                Select(AddAnnotation(new Annotation { Kind = Tool.Text, A = new Point(20, 20), Text = text.TrimEnd(), Style = _styles[Tool.Text] }));
                return;
            }
        }
        catch (Exception ex) { Log.Error("Paste text failed", ex); }
        StatusText.Text = "คลิปบอร์ดไม่มีรูปหรือข้อความ";
    }

    private void DeleteSelected()
    {
        if (_allSelected) { DeleteAllObjects(); return; }
        if (_selected is not { } e) return;
        int index = AnnotationLayer.Children.IndexOf(e);
        Select(null);
        AnnotationLayer.Children.Remove(e);
        _history.Push(new Edit(
            () => AnnotationLayer.Children.Insert(Math.Min(index, AnnotationLayer.Children.Count), e),
            () => { if (_selected == e) Select(null); AnnotationLayer.Children.Remove(e); }));
        UpdateChrome();
    }

    private void Duplicate()
    {
        if (_selected?.Tag is not Annotation a) return;
        var copy = a.Clone();
        copy.Offset += new Vector(20, 20);
        if (copy.Kind == Tool.Step) copy.Number = _nextStep++;
        Select(AddAnnotation(copy));
        UpdateChrome();
    }

    private void MoveZ(bool front)
    {
        if (_selected is not { } e) return;
        int from = AnnotationLayer.Children.IndexOf(e);
        int to = front ? AnnotationLayer.Children.Count - 1 : 0;
        if (from == to) return;
        void Move(int a, int b) { AnnotationLayer.Children.Remove(e); AnnotationLayer.Children.Insert(b, e); }
        Move(from, to);
        _history.Push(new Edit(() => Move(to, from), () => Move(from, to)));
    }

    private void Delete_Click(object sender, RoutedEventArgs e) => DeleteSelected();
    private void Duplicate_Click(object sender, RoutedEventArgs e) => Duplicate();
    private void BringToFront_Click(object sender, RoutedEventArgs e) => MoveZ(front: true);
    private void SendToBack_Click(object sender, RoutedEventArgs e) => MoveZ(front: false);
    private void EditText_Click(object sender, RoutedEventArgs e) { if (_selected is not null) BeginEdit(_selected); }

    // ================================================================== text editing

    private void BeginNewText(Point at, AnnotationStyle style)
    {
        _editingNew = new Annotation { Kind = Tool.Text, A = at, Style = style };
        _editingElement = null;
        ShowEditor(new Rect(at.X, at.Y, 0, 0), style, "", Tool.Text);
    }

    private void BeginEdit(FrameworkElement element)
    {
        if (element.Tag is not Annotation { HasText: true } a) return;
        _editingNew = null;
        _editingElement = element;
        var box = Annotations.Normalize(a.A, a.B);
        var bounds = a.Kind is Tool.Callout or Tool.Step
            ? new Rect(box.TopLeft + a.Offset, box.Size)
            : new Rect(a.A + a.Offset, new Size(0, 0));
        element.Visibility = Visibility.Hidden;
        ShowEditor(bounds, a.Style, a.Kind == Tool.Step ? a.StepLabel : a.Text, a.Kind);
    }

    private void ShowEditor(Rect at, AnnotationStyle style, string text, Tool kind)
    {
        bool step = kind == Tool.Step;
        bool callout = kind == Tool.Callout || step; // both edit inside their own box
        var fill = callout ? (style.Fill.A > 0 ? style.Fill : Colors.White) : Colors.Transparent;
        var box = new TextBox
        {
            Text = text, AcceptsReturn = !step, MinWidth = step ? 0 : 80,
            FontSize = step ? Math.Max(8, (at.Height - 6) / 1.7) : style.FontSize,
            FontFamily = new FontFamily("Segoe UI"), FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(callout ? Annotations.Contrast(fill) : style.Outline),
            Background = callout ? new SolidColorBrush(fill) : new SolidColorBrush(Color.FromArgb(0x30, 255, 255, 255)),
            BorderBrush = Brushes.DeepSkyBlue, BorderThickness = new Thickness(1), Padding = new Thickness(callout ? 8 : 0),
            TextWrapping = callout ? TextWrapping.Wrap : TextWrapping.NoWrap,
            TextAlignment = callout ? TextAlignment.Center : TextAlignment.Left,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        if (step)
        {
            box.Width = Math.Max(28, at.Width);
            box.Height = Math.Max(24, at.Height);
            box.Padding = new Thickness(0);
            box.MaxLength = 4;
            box.PreviewTextInput += StepDigits_PreviewTextInput;
        }
        else if (callout)
        {
            box.Width = Math.Max(80, at.Width);
            box.Height = Math.Max(30, at.Height);
        }
        Canvas.SetLeft(box, at.X);
        Canvas.SetTop(box, at.Y);
        box.KeyDown += (_, e) =>
        {
            // Esc always commits; for a step badge Enter does too (it is a one-line label).
            if (e.Key == Key.Escape || (step && e.Key == Key.Enter)) { CommitText(); e.Handled = true; }
        };
        box.LostKeyboardFocus += (_, _) => CommitText();
        InputLayer.Children.Add(box);
        _editor = box;
        Dispatcher.BeginInvoke(() => { box.Focus(); Keyboard.Focus(box); box.SelectAll(); });
    }

    /// <summary>
    /// Thai Kedmanee number-row keys (ๅ / - ภ ถ ุ ึ ค ต จ) and Thai digits (๐–๙) typed into a step badge
    /// become 1–9, 0, so numbering works without switching the keyboard to English.
    /// </summary>
    private static readonly System.Collections.Generic.Dictionary<char, char> ThaiDigitKeys = new()
    {
        ['ๅ'] = '1', ['/'] = '2', ['-'] = '3', ['ภ'] = '4', ['ถ'] = '5', ['ุ'] = '6', ['ึ'] = '7', ['ค'] = '8', ['ต'] = '9', ['จ'] = '0',
        ['๐'] = '0', ['๑'] = '1', ['๒'] = '2', ['๓'] = '3', ['๔'] = '4', ['๕'] = '5', ['๖'] = '6', ['๗'] = '7', ['๘'] = '8', ['๙'] = '9',
    };

    private void StepDigits_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (sender is not TextBox box || e.Text.Length != 1 || !ThaiDigitKeys.TryGetValue(e.Text[0], out char digit)) return;
        e.Handled = true;
        int caret = box.CaretIndex;
        box.SelectedText = digit.ToString();
        box.CaretIndex = Math.Min(box.Text.Length, caret + 1);
        box.SelectionLength = 0;
    }

    private void CommitText()
    {
        var box = _editor;
        if (box is null) return;
        _editor = null;
        InputLayer.Children.Remove(box);
        string text = box.Text.TrimEnd();

        if (_editingNew is { } fresh)
        {
            _editingNew = null;
            if (text.Length == 0) return;
            fresh.Text = text;
            AddAnnotation(fresh);
            return;
        }

        if (_editingElement is { Tag: Annotation { Kind: Tool.Step } s } stepElement)
        {
            _editingElement = null;
            stepElement.Visibility = Visibility.Visible;
            text = text.Trim();
            if (text.Length == 0 || text == s.StepLabel) return;
            var renumbered = s.Clone();
            if (int.TryParse(text, out int n) && n >= 0)
            {
                renumbered.Number = n;
                renumbered.Text = "";
                // Like Snagit: the next badge continues after the edited number.
                if (n >= _nextStep) _nextStep = n + 1;
            }
            else
            {
                renumbered.Text = text; // letters such as "A" are allowed
            }
            var rebuilt = Annotations.Build(renumbered);
            Swap(stepElement, rebuilt);
            RecordSwap(stepElement, rebuilt);
            UpdateChrome();
            return;
        }

        if (_editingElement is { Tag: Annotation a } element)
        {
            _editingElement = null;
            element.Visibility = Visibility.Visible;
            if (text == a.Text) return;
            if (text.Length == 0 && a.Kind == Tool.Text)
            {
                Select(element);
                DeleteSelected();
                return;
            }
            var updated = a.Clone();
            updated.Text = text;
            var fresh2 = Annotations.Build(updated);
            Swap(element, fresh2);
            RecordSwap(element, fresh2);
        }
    }

    // ================================================================== crop & image ops

    private void ApplyCrop(Int32Rect region) => ApplyCanvas(region, "ครอบตัด");

    /// <summary>
    /// Changes the canvas to <paramref name="region"/> (image pixels; may reach outside the picture,
    /// which becomes transparent). Annotations stay editable and keep their place on the picture.
    /// </summary>
    private void ApplyCanvas(Int32Rect region, string name)
    {
        if (region.Width < 2 || region.Height < 2) return;
        if (region.X == 0 && region.Y == 0 && region.Width == _base.PixelWidth && region.Height == _base.PixelHeight) return;
        var before = _base;
        var after = ImageOps.Canvas(before, region);
        StatusText.Text = $"{name}: {region.Width} × {region.Height} px";
        var shift = new Vector(-region.X, -region.Y);
        var items = AnnotationLayer.Children.OfType<FrameworkElement>().ToList();

        void Shift(Vector by)
        {
            foreach (var item in items)
                if (item.Tag is Annotation a) SetOffset(item, a.Offset + by);
        }

        Select(null);
        SetBase(after);
        Shift(shift);
        _history.Push(new Edit(
            () => { Select(null); SetBase(before); Shift(-shift); },
            () => { Select(null); SetBase(after); Shift(shift); }));
        SetTool(Tool.Select);
        Dispatcher.BeginInvoke(() => SetZoom(Math.Min(1 / DpiScale, FitScale())));
    }

    /// <summary>Flattens annotations into the image, then transforms the whole picture (undoable).</summary>
    private void ApplyImageOp(string name, Func<BitmapSource, BitmapSource?> op)
    {
        CommitText();
        BitmapSource? result;
        try { result = op(Flatten()); }
        catch (Exception ex)
        {
            Log.Error("Image operation failed: " + name, ex);
            StatusText.Text = $"{name} ไม่สำเร็จ: {ex.Message}";
            return;
        }
        if (result is null)
        {
            StatusText.Text = $"{name}: ไม่มีอะไรเปลี่ยน";
            return;
        }

        var oldBase = _base;
        var oldItems = AnnotationLayer.Children.OfType<UIElement>().ToList();
        void ToNew() => ResetContent(result);
        void ToOld() { Select(null); AnnotationLayer.Children.Clear(); foreach (var i in oldItems) AnnotationLayer.Children.Add(i); SetBase(oldBase); }
        ToNew();
        _history.Push(new Edit(ToOld, ToNew));
        StatusText.Text = name;
        Dispatcher.BeginInvoke(() => SetZoom(Math.Min(Zoom.ScaleX, FitScale())));
    }

    private void CropTool_Click(object sender, RoutedEventArgs e) { TabTools.IsChecked = true; SetTool(Tool.Crop); }
    private void Trim_Click(object sender, RoutedEventArgs e) => ApplyImageOp("ตัดขอบ", img => ImageOps.Trim(img));
    private void RotateLeft_Click(object sender, RoutedEventArgs e) => ApplyImageOp("หมุนซ้าย", img => ImageOps.Rotate(img, 270));
    private void RotateRight_Click(object sender, RoutedEventArgs e) => ApplyImageOp("หมุนขวา", img => ImageOps.Rotate(img, 90));
    private void FlipH_Click(object sender, RoutedEventArgs e) => ApplyImageOp("กลับซ้าย-ขวา", img => ImageOps.Flip(img, horizontal: true));
    private void FlipV_Click(object sender, RoutedEventArgs e) => ApplyImageOp("กลับบน-ล่าง", img => ImageOps.Flip(img, horizontal: false));
    private void ImageShadow_Click(object sender, RoutedEventArgs e) => ApplyImageOp("เงาภาพ", ImageOps.Shadow);
    private void Grayscale_Click(object sender, RoutedEventArgs e) => ApplyImageOp("ขาวดำ", ImageOps.Grayscale);

    private void Border_Click(object sender, RoutedEventArgs e)
    {
        var style = _styles[Tool.Rectangle];
        var color = style.Outline.A > 0 ? style.Outline : Colors.Black;
        int width = (int)Math.Max(2, style.Width * 2);
        ApplyImageOp("กรอบภาพ", img => ImageOps.Border(img, color, width));
    }

    private void Resize_Click(object sender, RoutedEventArgs e)
    {
        var percent = Prompt.ForNumber(this, "ปรับขนาดภาพ", $"ขนาดปัจจุบัน {_base.PixelWidth} × {_base.PixelHeight} px\nขนาดใหม่ (%)", 50, 1, 1000);
        if (percent is { } p && Math.Abs(p - 100) > 0.01)
            ApplyImageOp($"ปรับขนาด {p}%", img => ImageOps.Resize(img, p / 100));
    }

    // ================================================================== keyboard

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_editor is not null || e.OriginalSource is TextBox) return; // typing into text
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

        if (ctrl)
        {
            switch (e.Key)
            {
                case Key.Z: _history.Undo(); break;
                case Key.Y: _history.Redo(); break;
                case Key.A: SelectAll(); break;
                case Key.C: CopyToClipboard(); break;
                case Key.V: Paste(); break;
                case Key.D: Duplicate(); break;
                case Key.N: NewFromClipboard(); break;
                case Key.O: OpenFile(); break;
                case Key.P: Print(); break;
                case Key.S when shift: SaveAs(); break;
                case Key.S: Save(); break;
                case Key.D0: ZoomFit_Click(this, e); break;
                case Key.D1: ZoomActual_Click(this, e); break;
                default: return;
            }
            e.Handled = true;
            return;
        }

        Tool? tool = e.Key switch
        {
            Key.V => Tool.Select, Key.A => Tool.Arrow, Key.P => Tool.Pen, Key.H => Tool.Highlight, Key.K => Tool.Callout,
            Key.T => Tool.Text, Key.L => Tool.Line, Key.R => Tool.Rectangle, Key.E => Tool.Ellipse, Key.S => Tool.Step,
            Key.B => Tool.Blur, Key.C => Tool.Crop, _ => null,
        };
        if (tool is { } t) { SetTool(t); e.Handled = true; return; }

        if (e.Key is Key.Delete or Key.Back) { DeleteSelected(); e.Handled = true; }
        else if (e.Key == Key.Escape) { Select(null); SetTool(Tool.Select); e.Handled = true; }
        else if (_selected is { Tag: Annotation a } el && e.Key is Key.Left or Key.Right or Key.Up or Key.Down)
        {
            double step = shift ? 10 : 1;
            var delta = e.Key switch { Key.Left => new Vector(-step, 0), Key.Right => new Vector(step, 0), Key.Up => new Vector(0, -step), _ => new Vector(0, step) };
            var from = a.Offset;
            var to = from + delta;
            SetOffset(el, to);
            _history.Push(new Edit(() => SetOffset(el, from), () => SetOffset(el, to)));
            e.Handled = true;
        }
    }

    // ================================================================== output

    /// <summary>Renders the image plus every annotation at exact pixel size (zoom and screen DPI don't matter).</summary>
    public BitmapSource Flatten()
    {
        CommitText();
        int w = _base.PixelWidth, h = _base.PixelHeight;
        ImageHost.UpdateLayout();
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawImage(_base, new Rect(0, 0, w, h));
            if (AnnotationLayer.Children.Count > 0)
            {
                dc.DrawRectangle(new VisualBrush(AnnotationLayer)
                {
                    Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top,
                    ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(0, 0, w, h),
                    ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0, 0, w, h),
                }, null, new Rect(0, 0, w, h));
            }
        }
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        return rtb;
    }

    private void CopyToClipboard()
    {
        try
        {
            ClipboardHelper.SetImage(Flatten());
            StatusText.Text = "คัดลอกภาพไปยังคลิปบอร์ดแล้ว";
        }
        catch (Exception ex)
        {
            Log.Error("Copy failed", ex);
            MessageBox.Show(this, "คัดลอกไม่สำเร็จ: " + ex.Message, "SnagitCraft", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private bool Save() => _filePath is null ? SaveAs() : WriteTo(_filePath);

    private bool SaveAs()
    {
        var dlg = new SaveFileDialog
        {
            Filter = "PNG (*.png)|*.png|JPEG (*.jpg)|*.jpg|Bitmap (*.bmp)|*.bmp|GIF (*.gif)|*.gif",
            FileName = _filePath is null ? $"capture_{DateTime.Now:yyyyMMdd_HHmmss}.png" : System.IO.Path.GetFileName(_filePath),
            InitialDirectory = _filePath is null ? null : System.IO.Path.GetDirectoryName(_filePath),
        };
        return dlg.ShowDialog(this) == true && WriteTo(dlg.FileName);
    }

    private bool WriteTo(string path)
    {
        try
        {
            CaptureLibrary.Save(Flatten(), path);
            _filePath = path;
            _history.IsDirty = false;
            UpdateChrome();
            StatusText.Text = "บันทึกแล้ว: " + path;
            Saved?.Invoke(path);
            RefreshTray();
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Save failed", ex);
            MessageBox.Show(this, "บันทึกไม่สำเร็จ: " + ex.Message + "\n\nลอง \"บันทึกเป็น…\" ไปยังโฟลเดอร์อื่น",
                "SnagitCraft", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }

    private void NewFromClipboard()
    {
        try
        {
            if (!Clipboard.ContainsImage()) { StatusText.Text = "คลิปบอร์ดไม่มีรูปภาพ"; return; }
            var img = Clipboard.GetImage();
            if (img is null) return;
            LoadCapture(ScreenCapture.Materialize(img, new Int32Rect(0, 0, img.PixelWidth, img.PixelHeight)), null);
        }
        catch (Exception ex)
        {
            Log.Error("Paste failed", ex);
            StatusText.Text = "อ่านคลิปบอร์ดไม่ได้: " + ex.Message;
        }
    }

    private void OpenFile()
    {
        var dlg = new OpenFileDialog { Filter = "รูปภาพ|*.png;*.jpg;*.jpeg;*.bmp;*.gif|ทุกไฟล์|*.*" };
        if (dlg.ShowDialog(this) != true) return;
        try { LoadCapture(CaptureLibrary.Load(dlg.FileName), dlg.FileName); }
        catch (Exception ex) { MessageBox.Show(this, "เปิดไฟล์ไม่ได้: " + ex.Message, "SnagitCraft"); }
    }

    private void Print()
    {
        var dlg = new PrintDialog();
        if (dlg.ShowDialog() != true) return;
        var image = Flatten();
        double scale = Math.Min(1, Math.Min(dlg.PrintableAreaWidth / image.Width, dlg.PrintableAreaHeight / image.Height));
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
            dc.DrawImage(image, new Rect(0, 0, image.Width * scale, image.Height * scale));
        dlg.PrintVisual(visual, Title);
    }

    private void Reveal()
    {
        if (_filePath is null) { StatusText.Text = "ภาพนี้ยังไม่ได้บันทึก"; return; }
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_filePath}\"") { UseShellExecute = true });
    }

    private void Undo_Click(object sender, RoutedEventArgs e) => _history.Undo();
    private void Redo_Click(object sender, RoutedEventArgs e) => _history.Redo();
    private void CopyAll_Click(object sender, RoutedEventArgs e) => CopyToClipboard();
    private void Save_Click(object sender, RoutedEventArgs e) => Save();
    private void SaveAs_Click(object sender, RoutedEventArgs e) => SaveAs();
    private void Print_Click(object sender, RoutedEventArgs e) => Print();
    private void Reveal_Click(object sender, RoutedEventArgs e) => Reveal();
    private void Open_Click(object sender, RoutedEventArgs e) => OpenFile();
    private void NewFromClipboard_Click(object sender, RoutedEventArgs e) => NewFromClipboard();
    private void OpenLibrary_Click(object sender, RoutedEventArgs e) => _app.ShowMain();

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!ConfirmDiscard()) e.Cancel = true;
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        // Drop every reference to pixel data so a closed editor never holds memory.
        _history.Clear();
        AnnotationLayer.Children.Clear();
        BaseImage.Source = null;
        Tray.ItemsSource = null;
        base.OnClosed(e);
    }
}
