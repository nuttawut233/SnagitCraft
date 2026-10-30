using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SnagitAnything.Capture;
using SnagitAnything.Editor;
using SnagitAnything.Services;
using static SnagitAnything.Native.NativeMethods;

namespace SnagitAnything;

public partial class App : Application
{
    private Mutex? _singleInstance;
    private HotkeyManager? _fallbackHotkeys;
    private CaptureWidget? _widget;
    private bool _capturing;
    private TrayIcon? _tray;
    private MainWindow? _main;
    private EditorWindow? _editor;
    private bool _notified;

    public AppSettings Settings { get; private set; } = new();
    public CaptureLibrary Library { get; private set; } = null!;
    public bool IsExiting { get; private set; }

    protected override async void OnStartup(StartupEventArgs e)
    {
        // Keep WinForms (tray icon menu only) on the same Per-Monitor V2 DPI mode as the WPF windows.
        // Must run before any WinForms object exists.
        System.Windows.Forms.Application.SetHighDpiMode(System.Windows.Forms.HighDpiMode.PerMonitorV2);
        base.OnStartup(e);

        // Any failure is logged and reported; the app keeps running instead of disappearing.
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error("Unhandled UI exception", args.Exception);
            args.Handled = true;
            MessageBox.Show("เกิดข้อผิดพลาด แต่โปรแกรมยังทำงานต่อได้:\n" + args.Exception.Message + $"\n\nรายละเอียดอยู่ใน {Log.FilePath}",
                "SnagitCraft", MessageBoxButton.OK, MessageBoxImage.Warning);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log.Error("Fatal exception", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) => { Log.Error("Unobserved task exception", args.Exception); args.SetObserved(); };

        Settings = AppSettings.Load();
        Library = new CaptureLibrary(Settings);

        int stressIndex = Array.IndexOf(e.Args, "--stress");
        if (stressIndex >= 0)
        {
            int runs = stressIndex + 1 < e.Args.Length && int.TryParse(e.Args[stressIndex + 1], out int n) ? n : 20;
            int code = await RunStressAsync(runs);
            Shutdown(code);
            return;
        }

        _singleInstance = new Mutex(true, @"Local\SnagitAnything.SingleInstance", out bool first);
        if (!first)
        {
            // Already running (often minimized to the tray): ask that copy to show its window, then quit.
            try { EventWaitHandle.OpenExisting(ShowSignalName).Set(); }
            catch (WaitHandleCannotBeOpenedException) { MessageBox.Show("SnagitCraft เปิดอยู่แล้วที่ถาดระบบ", "SnagitCraft"); }
            Shutdown();
            return;
        }
        ListenForShowRequests();

        _main = new MainWindow(this);
        _tray = new TrayIcon(
            capture: () => StartCapture(CaptureProfile.Image),
            fullScreen: () => StartCapture(CaptureProfile.FullScreen),
            open: ShowMain,
            exit: ExitApp,
            settings: OpenSettings);

        Hotkeys = new GlobalHotkeys(Dispatcher);
        ApplySettings();

        Log.Info("Started");
        // Any image path on the command line ("Open with…") goes straight to the editor.
        var files = e.Args.Where(a => !a.StartsWith("--") && File.Exists(a)).ToList();
        foreach (var file in files) OpenEditor(file);
        if (files.Count == 0 && !Settings.StartMinimized && !e.Args.Contains("--minimized")) ShowMain();
    }

    /// <summary>The global keyboard hook (also used by Settings to record a new hotkey).</summary>
    public GlobalHotkeys? Hotkeys { get; private set; }

    /// <summary>Re-applies hotkeys, the capture widget and status text after settings change.</summary>
    public void ApplySettings()
    {
        var bindings = CaptureProfiles.All
            .Select(p => (Binding: Settings.HotkeyFor(p.Profile), p.Profile))
            .Where(x => x.Binding is not null)
            .Select(x => (x.Binding!, (Action)(() => StartCapture(x.Profile))))
            .ToList();

        string status;
        if (Hotkeys?.IsInstalled == true)
        {
            Hotkeys.SetBindings(bindings);
            _fallbackHotkeys?.Dispose();
            _fallbackHotkeys = null;
            status = bindings.Count == 0 ? "ยังไม่ได้ตั้งปุ่มลัด — ไปที่ ตั้งค่า"
                : "ปุ่มลัด: " + string.Join("  •  ", bindings.Select(b => b.Item1.Display));
        }
        else
        {
            // No keyboard hook (very locked-down machines): fall back to RegisterHotKey.
            _fallbackHotkeys?.Dispose();
            _fallbackHotkeys = new HotkeyManager();
            var ok = bindings.Where(b => _fallbackHotkeys.TryRegister(
                new Hotkey(b.Item1.ToString(), ToModFlags(b.Item1), (uint)b.Item1.VirtualKey, b.Item1.Display), b.Item2)).ToList();
            status = "ปุ่มลัด (โหมดสำรอง): " + string.Join("  •  ", ok.Select(b => b.Item1.Display));
        }
        _main?.SetHotkeyStatus(status);

        if (Settings.ShowCaptureWidget)
        {
            _widget ??= new CaptureWidget(this);
            _widget.RefreshProfiles();
            if (!_capturing) _widget.Show();
        }
        else
        {
            _widget?.Close();
            _widget = null;
        }
    }

    private static uint ToModFlags(HotkeyBinding b) =>
        (b.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Control) ? MOD_CONTROL : 0) |
        (b.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Shift) ? MOD_SHIFT : 0) |
        (b.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Alt) ? MOD_ALT : 0) |
        (b.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Windows) ? 0x8u : 0);

    public void OpenSettings()
    {
        var existing = Windows.OfType<SettingsWindow>().FirstOrDefault();
        if (existing is not null) { existing.Activate(); return; }
        new SettingsWindow(this).Show();
    }

    /// <summary>Widget's editor button: bring the editor forward, or open the newest capture in it.</summary>
    public void OpenLatestInEditor()
    {
        if (_editor is not null)
        {
            BringToFront(_editor);
            return;
        }
        var latest = Library.Recent(1).FirstOrDefault();
        if (latest is not null) OpenEditor(latest.FullName);
        else ShowMain();
    }

    private const string ShowSignalName = @"Local\SnagitAnything.ShowMain";

    /// <summary>A second launch (Start menu, desktop icon) signals this event; the running copy shows its main window.</summary>
    private void ListenForShowRequests()
    {
        var signal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSignalName);
        var thread = new Thread(() =>
        {
            while (signal.WaitOne())
                Dispatcher.BeginInvoke(ShowMain);
        }) { IsBackground = true, Name = "ShowMainSignal" };
        thread.Start();
    }

    public void ShowMain()
    {
        if (_main is null) return;
        BringToFront(_main);
    }

    public void NotifyOnce(string title, string text)
    {
        if (_notified) return;
        _notified = true;
        _tray?.Notify(title, text);
    }

    public async void StartCapture(CaptureProfile profile)
    {
        if (_capturing) return;
        _capturing = true;
        bool mainWasVisible = _main?.IsVisible == true;
        bool widgetWasVisible = _widget?.IsVisible == true;
        try
        {
            // Our own windows must not end up in the picture.
            if (mainWasVisible) _main!.Hide();
            if (widgetWasVisible) _widget!.Hide();
            if (mainWasVisible || widgetWasVisible) await Task.Delay(200); // let DWM repaint what was behind them

            var (mode, delay) = profile switch
            {
                CaptureProfile.Monitor => (CaptureMode.ActiveMonitor, Settings.DelaySeconds),
                CaptureProfile.FullScreen => (CaptureMode.FullScreen, Settings.DelaySeconds),
                CaptureProfile.Delayed => (CaptureMode.Interactive, CaptureProfiles.DelayedSeconds),
                _ => (CaptureMode.Interactive, Settings.DelaySeconds),
            };
            if (delay > 0) _tray?.Notify("SnagitCraft", $"จะจับภาพใน {delay} วินาที…");

            var image = await CaptureSession.RunAsync(mode, delay);
            if (image is null)
            {
                if (mainWasVisible) ShowMain();
                return;
            }
            if (profile == CaptureProfile.Clipboard) SendToClipboard(image);
            else HandleCapture(image, mainWasVisible);
        }
        catch (Exception ex)
        {
            Log.Error("Capture failed", ex);
            _tray?.Notify("จับภาพไม่สำเร็จ", ex.Message);
            if (mainWasVisible) ShowMain();
        }
        finally
        {
            _capturing = false;
            if (widgetWasVisible && _widget is not null) _widget.Show();
        }
    }

    private void SendToClipboard(BitmapSource image)
    {
        try
        {
            ClipboardHelper.SetImage(image);
            _tray?.Notify("คัดลอกไปคลิปบอร์ดแล้ว", $"{image.PixelWidth} × {image.PixelHeight} px — วาง (Ctrl+V) ได้เลย");
        }
        catch (Exception ex)
        {
            Log.Error("Clipboard failed", ex);
            _tray?.Notify("คัดลอกไม่สำเร็จ", ex.Message);
        }
    }

    private void HandleCapture(BitmapSource image, bool mainWasVisible)
    {
        // Each step stands alone: a failed save must not also lose the clipboard copy and the editor.
        string? path = null;
        if (Settings.AutoSave)
        {
            try
            {
                bool wasFallback = Library.UsingFallback;
                path = Library.SaveNew(image);
                Log.Info($"Saved {image.PixelWidth}x{image.PixelHeight} -> {path}");
                if (Library.UsingFallback && !wasFallback)
                {
                    string used = path;
                    // After the editor is up, so the capture itself is never held up by the dialog.
                    Dispatcher.BeginInvoke(() => WarnBlockedFolder(used), DispatcherPriority.ApplicationIdle);
                }
            }
            catch (Exception ex)
            {
                Log.Error("Save failed", ex);
                _tray?.Notify("บันทึกภาพอัตโนมัติไม่สำเร็จ", ex.Message);
            }
        }
        if (Settings.CopyToClipboard)
        {
            try { ClipboardHelper.SetImage(image); }
            catch (Exception ex) { Log.Error("Clipboard failed", ex); }
        }

        _main?.RefreshRecent();
        if (Settings.OpenEditor) OpenEditor(image, path);
        else if (mainWasVisible) ShowMain();
        else _tray?.Notify("จับภาพแล้ว", path ?? "คัดลอกไปคลิปบอร์ดแล้ว");
    }

    private void WarnBlockedFolder(string usedPath)
    {
        MessageBox.Show(
            $"Windows ไม่อนุญาตให้บันทึกลงโฟลเดอร์\n{Settings.SaveFolder}\n\n" +
            "สาเหตุมักเป็น Windows Security › Ransomware protection (Controlled folder access) ที่บล็อกโปรแกรมใหม่\n\n" +
            $"ครั้งนี้บันทึกไว้ที่\n{usedPath}\n\n" +
            "ถ้าต้องการเก็บในโฟลเดอร์เดิม ให้เพิ่ม SnagitAnything.exe ที่ Windows Security › Virus & threat protection › " +
            "Ransomware protection › Allow an app through Controlled folder access หรือกด \"เปลี่ยน…\" เพื่อเลือกโฟลเดอร์อื่น",
            "SnagitCraft", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    public void OpenEditor(string path)
    {
        try { OpenEditor(CaptureLibrary.Load(path), path); }
        catch (Exception ex)
        {
            Log.Error("Could not open " + path, ex);
            MessageBox.Show("เปิดไฟล์ไม่ได้: " + ex.Message, "SnagitCraft");
        }
    }

    /// <summary>One editor window for everything, like Snagit Editor; new captures load into it.</summary>
    private void OpenEditor(BitmapSource image, string? path)
    {
        if (_editor is null)
        {
            _editor = new EditorWindow(this);
            _editor.Saved += _ => _main?.RefreshRecent();
            _editor.Closed += (_, _) => _editor = null;
        }
        if (!_editor.LoadCapture(image, path)) return; // user kept the unsaved image
        BringToFront(_editor);
    }

    /// <summary>Shows a window un-minimized and in front (Windows may apply the launch's minimized state on first show).</summary>
    private static void BringToFront(Window w)
    {
        w.Show();
        var hwnd = new System.Windows.Interop.WindowInteropHelper(w).Handle;
        if (IsIconic(hwnd)) ShowWindow(hwnd, 9 /* SW_RESTORE */);
        if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
        w.Activate();
        SetForegroundWindow(hwnd);
    }

    private void ExitApp()
    {
        IsExiting = true;
        foreach (Window w in Windows.Cast<Window>().ToList()) w.Close();
        // An editor may have cancelled closing (unsaved changes); stay alive in that case.
        if (Windows.OfType<EditorWindow>().Any())
        {
            IsExiting = false;
            return;
        }
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Hotkeys?.Dispose();
        _fallbackHotkeys?.Dispose();
        _tray?.Dispose();
        _singleInstance?.Dispose();
        Log.Info("Exited");
        base.OnExit(e);
    }

    /// <summary>
    /// Reproduces the Snagit 12 crash scenario: many back-to-back interactive captures at the current
    /// (high) resolution, each one rendering the full-screen overlays. Records memory after every run.
    /// </summary>
    private async Task<int> RunStressAsync(int runs)
    {
        var report = new StringBuilder();
        var vs = ScreenCapture.GetVirtualScreen();
        report.AppendLine($"SnagitCraft stress test — {runs} captures, virtual screen {vs.Width}x{vs.Height}, " +
                          $"{ScreenCapture.GetMonitors().Count} monitor(s), {(Environment.Is64BitProcess ? "64" : "32")}-bit");
        string outDir = Path.Combine(Path.GetTempPath(), "SnagitAnything-stress");
        Directory.CreateDirectory(outDir);
        var proc = Process.GetCurrentProcess();
        int failures = 0;
        long firstPrivate = 0, peakPrivate = 0;

        for (int i = 1; i <= runs; i++)
        {
            try
            {
                var image = await CaptureSession.RunAutomatedAsync(TimeSpan.FromMilliseconds(250));
                if (image is null) throw new InvalidOperationException("capture returned nothing");
                CaptureLibrary.Save(image, Path.Combine(outDir, $"run{i:000}.png"));
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

                proc.Refresh();
                long priv = proc.PrivateMemorySize64 / (1024 * 1024);
                if (i == 1) firstPrivate = priv;
                peakPrivate = Math.Max(peakPrivate, priv);
                report.AppendLine($"run {i,3}: ok  {image.PixelWidth}x{image.PixelHeight}  private={priv} MB  managed={GC.GetTotalMemory(false) / (1024 * 1024)} MB");
            }
            catch (Exception ex)
            {
                failures++;
                report.AppendLine($"run {i,3}: FAILED  {ex.GetType().Name}: {ex.Message}");
                Log.Error($"Stress run {i} failed", ex);
            }
        }

        report.AppendLine($"result: {runs - failures}/{runs} ok, private memory first={firstPrivate} MB peak={peakPrivate} MB");
        string reportPath = Path.Combine(outDir, "report.txt");
        File.WriteAllText(reportPath, report.ToString());
        Log.Info("Stress test finished: " + reportPath);
        return failures == 0 ? 0 : 1;
    }
}
