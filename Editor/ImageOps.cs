using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using SnagitAnything.Capture;

namespace SnagitAnything.Editor;

/// <summary>Whole-image operations for the Image tab. Each returns a new frozen 96-DPI bitmap.</summary>
public static class ImageOps
{
    public static BitmapSource Rotate(BitmapSource src, int degrees) =>
        Copy(new TransformedBitmap(src, new RotateTransform(degrees)));

    public static BitmapSource Flip(BitmapSource src, bool horizontal) =>
        Copy(new TransformedBitmap(src, new ScaleTransform(horizontal ? -1 : 1, horizontal ? 1 : -1)));

    public static BitmapSource Resize(BitmapSource src, double factor)
    {
        int w = Math.Max(1, (int)Math.Round(src.PixelWidth * factor));
        int h = Math.Max(1, (int)Math.Round(src.PixelHeight * factor));
        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
        using (var dc = visual.RenderOpen()) dc.DrawImage(src, new Rect(0, 0, w, h));
        return Render(visual, w, h);
    }

    public static BitmapSource Border(BitmapSource src, Color color, int width)
    {
        int w = src.PixelWidth + width * 2, h = src.PixelHeight + width * 2;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(color), null, new Rect(0, 0, w, h));
            dc.DrawImage(src, new Rect(width, width, src.PixelWidth, src.PixelHeight));
        }
        return Render(visual, w, h);
    }

    /// <summary>Drop shadow on a white margin, so it also looks right when pasted into apps without alpha.</summary>
    public static BitmapSource Shadow(BitmapSource src)
    {
        const int margin = 24;
        int w = src.PixelWidth + margin * 2, h = src.PixelHeight + margin * 2;
        var image = new Image
        {
            Source = src, Width = src.PixelWidth, Height = src.PixelHeight,
            Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 6, Direction = 315, Opacity = 0.45 },
        };
        var host = new Grid { Width = w, Height = h, Background = Brushes.White };
        host.Children.Add(image);
        host.Measure(new Size(w, h));
        host.Arrange(new Rect(0, 0, w, h));
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(host);
        return Copy(rtb);
    }

    public static BitmapSource Grayscale(BitmapSource src) =>
        Copy(new FormatConvertedBitmap(src, PixelFormats.Gray8, null, 0));

    /// <summary>Crops away uniform borders that match the top-left pixel colour (Snagit's "Trim").</summary>
    public static BitmapSource? Trim(BitmapSource src, int tolerance = 8)
    {
        var bgra = src.Format == PixelFormats.Bgra32 ? src : new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        int w = bgra.PixelWidth, h = bgra.PixelHeight, stride = w * 4;
        var px = new byte[stride * h];
        bgra.CopyPixels(px, stride, 0);

        bool Same(int x, int y) =>
            Math.Abs(px[y * stride + x * 4] - px[0]) <= tolerance &&
            Math.Abs(px[y * stride + x * 4 + 1] - px[1]) <= tolerance &&
            Math.Abs(px[y * stride + x * 4 + 2] - px[2]) <= tolerance;
        bool RowSame(int y) { for (int x = 0; x < w; x++) if (!Same(x, y)) return false; return true; }
        bool ColSame(int x, int top, int bottom) { for (int y = top; y <= bottom; y++) if (!Same(x, y)) return false; return true; }

        int t = 0, b = h - 1;
        while (t < b && RowSame(t)) t++;
        while (b > t && RowSame(b)) b--;
        int l = 0, r = w - 1;
        while (l < r && ColSame(l, t, b)) l++;
        while (r > l && ColSame(r, t, b)) r--;

        if (l == 0 && t == 0 && r == w - 1 && b == h - 1) return null; // nothing to trim
        return ScreenCapture.Materialize(src, new Int32Rect(l, t, r - l + 1, b - t + 1));
    }

    private static BitmapSource Render(Visual visual, int w, int h)
    {
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        return Copy(rtb);
    }

    /// <summary>
    /// New canvas of <paramref name="area"/> (in source pixel coordinates; may start negative or extend past
    /// the picture). Pixels outside the original become transparent — Snagit's canvas handles.
    /// </summary>
    public static BitmapSource Canvas(BitmapSource src, Int32Rect area)
    {
        var bgra = src.Format == PixelFormats.Bgra32 ? src : new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        int sw = bgra.PixelWidth, sh = bgra.PixelHeight;
        int w = Math.Max(1, area.Width), h = Math.Max(1, area.Height);
        var srcPx = new byte[sw * 4 * sh];
        bgra.CopyPixels(srcPx, sw * 4, 0);
        var dst = new byte[w * 4 * h];

        // Overlap of the new canvas with the old picture, in source coordinates.
        int x0 = Math.Max(0, area.X), y0 = Math.Max(0, area.Y);
        int x1 = Math.Min(sw, area.X + w), y1 = Math.Min(sh, area.Y + h);
        for (int y = y0; y < y1; y++)
        {
            Buffer.BlockCopy(srcPx, (y * sw + x0) * 4, dst, ((y - area.Y) * w + (x0 - area.X)) * 4, Math.Max(0, x1 - x0) * 4);
        }
        var result = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, dst, w * 4);
        result.Freeze();
        return result;
    }

    /// <summary>A fully transparent canvas.</summary>
    public static BitmapSource Blank(int width, int height)
    {
        var b = BitmapSource.Create(Math.Max(1, width), Math.Max(1, height), 96, 96, PixelFormats.Bgra32, null,
            new byte[Math.Max(1, width) * Math.Max(1, height) * 4], Math.Max(1, width) * 4);
        b.Freeze();
        return b;
    }

    /// <summary>Composites onto white — for targets without transparency (JPEG, BMP, most clipboard readers).</summary>
    public static BitmapSource OnWhite(BitmapSource src)
    {
        int w = src.PixelWidth, h = src.PixelHeight;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, w, h));
            dc.DrawImage(src, new Rect(0, 0, w, h));
        }
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        return rtb;
    }

    private static BitmapSource Copy(BitmapSource s) =>
        ScreenCapture.Materialize(s, new Int32Rect(0, 0, s.PixelWidth, s.PixelHeight), forceOpaque: false);
}
