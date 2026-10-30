using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using static SnagitAnything.Native.NativeMethods;

namespace SnagitAnything.Capture;

public enum CaptureMode
{
    /// <summary>Snagit-style all-in-one: drag for a region, click for a window.</summary>
    Interactive,
    FullScreen,
    ActiveMonitor,
}

/// <summary>
/// One capture from start to finish. Each run builds its snapshot and overlays from scratch and tears
/// them all down before returning, so the second, third, hundredth capture starts from the same clean
/// state as the first one — the failure mode behind the Snagit 12 crash was state left over between runs.
/// </summary>
public static class CaptureSession
{
    private static bool _running;

    public static async Task<BitmapSource?> RunAsync(CaptureMode mode, int delaySeconds = 0)
    {
        if (_running) return null;
        _running = true;
        try
        {
            if (delaySeconds > 0)
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds));

            ScreenSnapshot snapshot = ScreenCapture.TakeSnapshot();

            Int32Rect? chosen = mode switch
            {
                CaptureMode.FullScreen => snapshot.VirtualBounds,
                CaptureMode.ActiveMonitor => MonitorUnderCursor(snapshot).Bounds,
                _ => await PickAsync(snapshot),
            };

            if (chosen is not { } rect) return null;
            return Crop(snapshot, rect);
        }
        finally
        {
            _running = false;
        }
    }

    /// <summary>Cuts an absolute physical rectangle out of the snapshot into an independent bitmap.</summary>
    public static BitmapSource Crop(ScreenSnapshot snapshot, Int32Rect absolute)
    {
        var relative = new Int32Rect(absolute.X - snapshot.VirtualBounds.X, absolute.Y - snapshot.VirtualBounds.Y,
            absolute.Width, absolute.Height);
        return ScreenCapture.Materialize(snapshot.Image, relative);
    }

    private static MonitorInfo MonitorUnderCursor(ScreenSnapshot snapshot)
    {
        GetCursorPos(out var p);
        return snapshot.Monitors.FirstOrDefault(m =>
                   p.X >= m.Bounds.X && p.X < m.Bounds.X + m.Bounds.Width &&
                   p.Y >= m.Bounds.Y && p.Y < m.Bounds.Y + m.Bounds.Height)
               ?? snapshot.Monitors.First(m => m.IsPrimary);
    }

    /// <summary>
    /// Self-test: runs a full interactive session (snapshot + overlays rendered on every monitor) and
    /// auto-picks the monitor under the cursor after <paramref name="showFor"/>. Used by --stress.
    /// </summary>
    public static async Task<BitmapSource?> RunAutomatedAsync(TimeSpan showFor)
    {
        var snapshot = ScreenCapture.TakeSnapshot();
        var rect = await PickAsync(snapshot, showFor);
        return rect is { } r ? Crop(snapshot, r) : null;
    }

    private static Task<Int32Rect?> PickAsync(ScreenSnapshot snapshot, TimeSpan? autoPick = null)
    {
        var tcs = new TaskCompletionSource<Int32Rect?>();
        var overlays = new List<CaptureOverlayWindow>();

        void Finish(Int32Rect? result)
        {
            if (tcs.Task.IsCompleted) return;
            foreach (var o in overlays) o.Close();
            overlays.Clear();
            tcs.TrySetResult(result);
        }

        foreach (var monitor in snapshot.Monitors)
        {
            var overlay = new CaptureOverlayWindow(snapshot, monitor);
            overlay.Finished += Finish;
            overlay.FullScreenRequested += () => Finish(snapshot.VirtualBounds);
            overlays.Add(overlay);
        }

        foreach (var o in overlays) o.Show();

        // Give keyboard focus to the overlay under the cursor so Esc / F work immediately.
        var focusMonitor = MonitorUnderCursor(snapshot);
        var focus = overlays.FirstOrDefault(o => snapshot.Monitors[overlays.IndexOf(o)] == focusMonitor) ?? overlays[0];
        focus.Activate();
        Keyboard.Focus(focus);

        if (autoPick is { } delay)
        {
            _ = Task.Delay(delay).ContinueWith(_ =>
            {
                // A picker that doesn't cover its whole monitor lets clicks fall through to other apps.
                var bad = overlays.Where(o => !o.CoversMonitor())
                    .Select(o => $"{o.Monitor.Device} {o.Monitor.Bounds.Width}x{o.Monitor.Bounds.Height}").ToList();
                if (bad.Count == 0)
                {
                    Finish(focusMonitor.Bounds);
                    return;
                }
                foreach (var o in overlays) o.Close();
                overlays.Clear();
                tcs.TrySetException(new InvalidOperationException("overlay does not cover monitor: " + string.Join(", ", bad)));
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        return tcs.Task;
    }
}
