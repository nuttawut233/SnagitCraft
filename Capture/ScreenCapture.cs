using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SnagitAnything.Native;
using static SnagitAnything.Native.NativeMethods;

namespace SnagitAnything.Capture;

/// <summary>A monitor in physical (device) pixels.</summary>
public sealed record MonitorInfo(Int32Rect Bounds, double Scale, bool IsPrimary, string Device);

/// <summary>A top-level window that can be picked in window-capture mode.</summary>
public sealed record WindowInfo(IntPtr Handle, Int32Rect Bounds, string Title);

/// <summary>
/// A frozen picture of the whole virtual desktop, taken once per capture session.
/// Everything is in physical pixels; Origin is the virtual-screen top-left (can be negative).
/// </summary>
public sealed class ScreenSnapshot
{
    public required BitmapSource Image { get; init; }
    public required Int32Rect VirtualBounds { get; init; }
    public required IReadOnlyList<MonitorInfo> Monitors { get; init; }
    public required IReadOnlyList<WindowInfo> Windows { get; init; }
}

public static class ScreenCapture
{
    public static IReadOnlyList<MonitorInfo> GetMonitors()
    {
        var list = new List<MonitorInfo>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMon, IntPtr _, ref RECT _, IntPtr _) =>
        {
            var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfo(hMon, ref mi))
            {
                double scale = 1.0;
                if (GetDpiForMonitor(hMon, 0, out uint dpiX, out _) == 0 && dpiX > 0)
                    scale = dpiX / 96.0;
                var r = mi.rcMonitor;
                list.Add(new MonitorInfo(new Int32Rect(r.Left, r.Top, r.Width, r.Height), scale,
                    (mi.dwFlags & 1) != 0, mi.szDevice));
            }
            return true;
        }, IntPtr.Zero);
        return list;
    }

    public static Int32Rect GetVirtualScreen() => new(
        GetSystemMetrics(SM_XVIRTUALSCREEN), GetSystemMetrics(SM_YVIRTUALSCREEN),
        GetSystemMetrics(SM_CXVIRTUALSCREEN), GetSystemMetrics(SM_CYVIRTUALSCREEN));

    /// <summary>Takes one snapshot of every monitor plus the window list (in z-order, topmost first).</summary>
    public static ScreenSnapshot TakeSnapshot()
    {
        // Enumerate windows before any overlay exists so our own overlays never show up in the list.
        var windows = EnumerateWindows();
        var vs = GetVirtualScreen();
        return new ScreenSnapshot
        {
            Image = CaptureRect(vs),
            VirtualBounds = vs,
            Monitors = GetMonitors(),
            Windows = windows,
        };
    }

    /// <summary>
    /// BitBlt a physical-pixel rectangle of the desktop into a frozen, fully managed bitmap.
    /// The GDI bitmap is released before returning, so nothing native outlives the call.
    /// </summary>
    public static BitmapSource CaptureRect(Int32Rect rect)
    {
        IntPtr screenDc = GetDC(IntPtr.Zero);
        IntPtr memDc = CreateCompatibleDC(screenDc);
        IntPtr hBitmap = CreateCompatibleBitmap(screenDc, rect.Width, rect.Height);
        if (hBitmap == IntPtr.Zero)
        {
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
            throw new InvalidOperationException($"Could not allocate a {rect.Width}x{rect.Height} screen bitmap.");
        }

        IntPtr old = SelectObject(memDc, hBitmap);
        try
        {
            BitBlt(memDc, 0, 0, rect.Width, rect.Height, screenDc, rect.X, rect.Y, SRCCOPY | CAPTUREBLT);
            SelectObject(memDc, old);
            var interop = Imaging.CreateBitmapSourceFromHBitmap(hBitmap, IntPtr.Zero, Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            return Materialize(interop, new Int32Rect(0, 0, rect.Width, rect.Height));
        }
        finally
        {
            DeleteObject(hBitmap);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    /// <summary>
    /// Copies a region into a brand-new 96-DPI bitmap. Unlike CroppedBitmap, the result does not keep
    /// the (possibly huge) source alive, so the full-desktop snapshot can be collected right after a capture.
    /// </summary>
    /// <param name="forceOpaque">
    /// True for screen / clipboard pixels, whose alpha byte is garbage. False for images we rendered
    /// ourselves, where transparency is real (e.g. canvas extended past the original picture).
    /// </param>
    public static BitmapSource Materialize(BitmapSource source, Int32Rect region, bool forceOpaque = true)
    {
        region = Clamp(region, source.PixelWidth, source.PixelHeight);
        var format = PixelFormats.Bgra32;
        BitmapSource converted = source.Format == format ? source : new FormatConvertedBitmap(source, format, null, 0);

        int stride = region.Width * 4;
        var pixels = new byte[stride * region.Height];
        converted.CopyPixels(region, pixels, stride, 0);

        // Screen pixels have no alpha; force opaque so PNGs don't come out transparent.
        if (forceOpaque)
            for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;

        var result = BitmapSource.Create(region.Width, region.Height, 96, 96, format, null, pixels, stride);
        result.Freeze();
        return result;
    }

    public static Int32Rect Clamp(Int32Rect r, int width, int height)
    {
        int x = Math.Clamp(r.X, 0, Math.Max(0, width - 1));
        int y = Math.Clamp(r.Y, 0, Math.Max(0, height - 1));
        int w = Math.Clamp(r.Width, 1, width - x);
        int h = Math.Clamp(r.Height, 1, height - y);
        return new Int32Rect(x, y, w, h);
    }

    private static List<WindowInfo> EnumerateWindows()
    {
        var result = new List<WindowInfo>();
        uint ownPid = (uint)Environment.ProcessId;
        EnumWindows((hWnd, _) =>
        {
            if (!IsWindowVisible(hWnd) || IsIconic(hWnd)) return true;
            GetWindowThreadProcessId(hWnd, out uint pid);
            if (pid == ownPid) return true;

            if (DwmGetWindowAttribute(hWnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
                return true;

            long exStyle = GetWindowLongPtr(hWnd, GWL_EXSTYLE).ToInt64();
            var title = new StringBuilder(256);
            GetWindowText(hWnd, title, title.Capacity);
            if ((exStyle & WS_EX_TOOLWINDOW) != 0 && title.Length == 0) return true;

            // Extended frame bounds exclude the invisible resize borders Windows 10/11 adds.
            if (DwmGetWindowAttribute(hWnd, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT r, Marshal.SizeOf<RECT>()) != 0)
                GetWindowRect(hWnd, out r);
            if (r.Width < 8 || r.Height < 8) return true;

            result.Add(new WindowInfo(hWnd, new Int32Rect(r.Left, r.Top, r.Width, r.Height), title.ToString()));
            return true;
        }, IntPtr.Zero);
        return result;
    }

    [Conditional("DEBUG")]
    public static void Trace(string message) => Debug.WriteLine("[capture] " + message);
}
