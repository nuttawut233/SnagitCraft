using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using SnagitAnything.Capture;

namespace SnagitAnything.Editor;

/// <summary>Drawing tools; <see cref="Picture"/> is not a tool but the kind of an image pasted with Ctrl+V.</summary>
public enum Tool { Select, Arrow, Pen, Highlight, Callout, Text, Line, Rectangle, Ellipse, Step, Blur, Crop, Picture }

public enum StepShape { Circle, Square }

/// <summary>How an annotation looks. Fill = Transparent means "no fill".</summary>
public sealed record AnnotationStyle(Color Outline, Color Fill, double Width, bool Shadow, double FontSize, StepShape StepShape = StepShape.Circle);

/// <summary>
/// One object on the canvas, kept as data so it can be restyled, resized and re-edited at any time.
/// The WPF element on screen is always rebuilt from this (see <see cref="Annotations.Build"/>).
/// All coordinates are image pixels; <see cref="Offset"/> is where the user has moved it to.
/// </summary>
public sealed class Annotation
{
    public required Tool Kind { get; init; }
    public Point A { get; set; }
    public Point B { get; set; }
    public List<Point> Points { get; set; } = new();
    public string Text { get; set; } = "";
    public int Number { get; set; }
    public required AnnotationStyle Style { get; set; }
    public Vector Offset { get; set; }
    public BitmapSource? Pixels { get; set; }

    /// <summary>Two draggable end points (corners for boxes, ends for lines).</summary>
    public bool HasHandles => Kind is Tool.Arrow or Tool.Line or Tool.Rectangle or Tool.Ellipse or Tool.Highlight or Tool.Callout or Tool.Step or Tool.Picture;
    public bool HasText => Kind is Tool.Text or Tool.Callout or Tool.Step;

    /// <summary>What a step badge shows: a custom label (e.g. "A") when set, otherwise its number.</summary>
    public string StepLabel => string.IsNullOrEmpty(Text) ? Number.ToString() : Text;

    public Annotation Clone()
    {
        var c = (Annotation)MemberwiseClone();
        c.Points = new List<Point>(Points);
        return c;
    }
}

public static class Annotations
{
    public static readonly Color Red = Color.FromRgb(0xE5, 0x39, 0x35);
    public static readonly Color Orange = Color.FromRgb(0xFF, 0x8A, 0x00);
    public static readonly Color Yellow = Color.FromRgb(0xFD, 0xD8, 0x35);
    public static readonly Color Green = Color.FromRgb(0x43, 0xA0, 0x47);
    public static readonly Color Blue = Color.FromRgb(0x1E, 0x88, 0xE5);
    public static readonly Color Purple = Color.FromRgb(0x8E, 0x24, 0xAA);
    public static readonly Color Pink = Color.FromRgb(0xEC, 0x40, 0x7A);
    public static readonly Color None = Colors.Transparent;

    public static readonly Color[] Palette =
    {
        Red, Orange, Yellow, Green, Blue, Purple, Pink,
        Colors.Black, Color.FromRgb(0x60, 0x60, 0x60), Color.FromRgb(0xBD, 0xBD, 0xBD), Colors.White,
    };

    /// <summary>Quick Styles gallery for each tool — the first entry is the default.</summary>
    public static IReadOnlyList<AnnotationStyle> Presets(Tool tool) => tool switch
    {
        Tool.Arrow or Tool.Line => new AnnotationStyle[]
        {
            new(Red, None, 5, true, 20), new(Yellow, None, 5, true, 20), new(Blue, None, 5, true, 20),
            new(Green, None, 5, true, 20), new(Colors.Black, None, 7, false, 20), new(Colors.White, None, 5, true, 20),
            new(Red, None, 10, true, 20), new(Orange, None, 3, false, 20),
        },
        Tool.Pen => new AnnotationStyle[]
        {
            new(Red, None, 4, false, 20), new(Blue, None, 4, false, 20), new(Colors.Black, None, 3, false, 20),
            new(Green, None, 6, false, 20), new(Yellow, None, 6, true, 20), new(Colors.White, None, 4, true, 20),
        },
        Tool.Rectangle or Tool.Ellipse => new AnnotationStyle[]
        {
            new(Red, None, 4, true, 20), new(Yellow, None, 4, true, 20), new(Blue, None, 4, true, 20),
            new(Green, None, 4, true, 20), new(Red, Color.FromArgb(0x33, 0xE5, 0x39, 0x35), 3, false, 20),
            new(Colors.Black, Color.FromArgb(0x55, 0xFD, 0xD8, 0x35), 2, false, 20), new(Colors.White, None, 4, true, 20),
            new(None, Colors.Black, 0, false, 20),
        },
        Tool.Highlight => new AnnotationStyle[]
        {
            new(Yellow, None, 0, false, 20), new(Color.FromRgb(0x76, 0xFF, 0x03), None, 0, false, 20),
            new(Pink, None, 0, false, 20), new(Color.FromRgb(0x40, 0xC4, 0xFF), None, 0, false, 20),
            new(Orange, None, 0, false, 20),
        },
        Tool.Text => new AnnotationStyle[]
        {
            new(Red, None, 0, false, 24), new(Colors.Black, None, 0, false, 24), new(Colors.White, None, 0, true, 24),
            new(Blue, None, 0, false, 22), new(Yellow, None, 0, true, 28), new(Green, None, 0, false, 22),
        },
        Tool.Callout => new AnnotationStyle[]
        {
            new(Colors.Black, Colors.White, 2, true, 18), new(Color.FromRgb(0x9E, 0x7C, 0x00), Yellow, 2, true, 18),
            new(Colors.White, Red, 2, true, 18), new(Colors.White, Blue, 2, true, 18),
            new(Colors.White, Color.FromRgb(0x21, 0x21, 0x21), 2, true, 18), new(Red, Colors.White, 3, false, 18),
        },
        Tool.Step => new AnnotationStyle[]
        {
            new(Colors.White, Red, 2, true, 20, StepShape.Circle), new(Colors.White, Blue, 2, true, 20, StepShape.Square),
            new(Color.FromRgb(0x9E, 0x7C, 0x00), Yellow, 2, true, 20, StepShape.Circle),
            new(Colors.Black, Colors.White, 2, true, 20, StepShape.Circle), new(Colors.White, Green, 2, true, 20, StepShape.Circle),
            new(Colors.White, Colors.Black, 2, false, 20, StepShape.Square),
        },
        _ => Array.Empty<AnnotationStyle>(),
    };

    // ------------------------------------------------------------------ building elements

    public static FrameworkElement Build(Annotation a)
    {
        FrameworkElement e = a.Kind switch
        {
            Tool.Arrow => BuildArrow(a),
            Tool.Line => BuildLine(a),
            Tool.Pen => BuildPen(a),
            Tool.Rectangle or Tool.Ellipse or Tool.Highlight => BuildBox(a),
            Tool.Callout => BuildCallout(a),
            Tool.Text => BuildText(a),
            Tool.Step => BuildStep(a),
            Tool.Blur => BuildBlur(a),
            Tool.Picture => BuildPicture(a),
            _ => throw new ArgumentOutOfRangeException(nameof(a), a.Kind, null),
        };
        e.Tag = a;
        e.RenderTransform = new TranslateTransform(a.Offset.X, a.Offset.Y);
        if (a.Style.Shadow)
            e.Effect = new DropShadowEffect { BlurRadius = 7, ShadowDepth = 2.5, Direction = 315, Opacity = 0.5 };
        return e;
    }

    private static Brush Solid(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    /// <summary>Black or white, whichever reads better on <paramref name="background"/>.</summary>
    public static Color Contrast(Color background) =>
        (0.299 * background.R + 0.587 * background.G + 0.114 * background.B) / 255 > 0.6 ? Colors.Black : Colors.White;

    private static Path BuildArrow(Annotation a)
    {
        var path = new Path
        {
            Stroke = Solid(a.Style.Outline), Fill = Solid(a.Style.Outline), StrokeThickness = a.Style.Width,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
        };
        var v = a.B - a.A;
        double len = v.Length;
        var g = new GeometryGroup { FillRule = FillRule.Nonzero };
        if (len >= 0.5)
        {
            v /= len;
            double head = Math.Min(len, a.Style.Width * 3.2 + 10);
            var normal = new Vector(-v.Y, v.X);
            var baseCenter = a.B - v * head;
            // Stop the shaft inside the head so the round cap doesn't poke through the tip.
            g.Children.Add(new LineGeometry(a.A, baseCenter + v * (head * 0.3)));
            var tri = new StreamGeometry();
            using (var ctx = tri.Open())
            {
                ctx.BeginFigure(a.B, isFilled: true, isClosed: true);
                ctx.LineTo(baseCenter + normal * head * 0.5, true, false);
                ctx.LineTo(baseCenter - normal * head * 0.5, true, false);
            }
            g.Children.Add(tri);
        }
        path.Data = g;
        return path;
    }

    private static Line BuildLine(Annotation a) => new()
    {
        X1 = a.A.X, Y1 = a.A.Y, X2 = a.B.X, Y2 = a.B.Y,
        Stroke = Solid(a.Style.Outline), StrokeThickness = a.Style.Width,
        StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
    };

    private static Polyline BuildPen(Annotation a) => new()
    {
        Points = new PointCollection(a.Points),
        Stroke = Solid(a.Style.Outline), StrokeThickness = a.Style.Width,
        StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
    };

    private static Shape BuildBox(Annotation a)
    {
        Shape s = a.Kind == Tool.Ellipse ? new Ellipse() : new Rectangle();
        if (a.Kind == Tool.Highlight)
        {
            s.Fill = Solid(Color.FromArgb(0x70, a.Style.Outline.R, a.Style.Outline.G, a.Style.Outline.B));
        }
        else
        {
            if (a.Style.Width > 0 && a.Style.Outline.A > 0)
            {
                s.Stroke = Solid(a.Style.Outline);
                s.StrokeThickness = a.Style.Width;
            }
            if (a.Style.Fill.A > 0) s.Fill = Solid(a.Style.Fill);
            if (s is Rectangle r) r.RadiusX = r.RadiusY = Math.Min(4, a.Style.Width);
        }
        Place(s, Normalize(a.A, a.B));
        return s;
    }

    private static Canvas BuildCallout(Annotation a)
    {
        var rect = Normalize(a.A, a.B);
        double w = Math.Max(40, rect.Width), h = Math.Max(28, rect.Height);
        // A Canvas (not a Grid) so the tail, which hangs below the box, is not clipped by layout.
        var root = new Canvas { Width = w, Height = h };
        Canvas.SetLeft(root, rect.X);
        Canvas.SetTop(root, rect.Y);

        // Rounded bubble with a tail hanging off the bottom-left.
        double r = Math.Min(12, Math.Min(w, h) / 3);
        double tailX = Math.Min(w * 0.25 + 10, w - r - 16);
        var geo = new StreamGeometry();
        using (var c = geo.Open())
        {
            c.BeginFigure(new Point(r, 0), true, true);
            c.LineTo(new Point(w - r, 0), true, true);
            c.ArcTo(new Point(w, r), new Size(r, r), 0, false, SweepDirection.Clockwise, true, true);
            c.LineTo(new Point(w, h - r), true, true);
            c.ArcTo(new Point(w - r, h), new Size(r, r), 0, false, SweepDirection.Clockwise, true, true);
            c.LineTo(new Point(tailX + 16, h), true, true);
            c.LineTo(new Point(tailX - 14, h + 26), true, true);
            c.LineTo(new Point(tailX, h), true, true);
            c.LineTo(new Point(r, h), true, true);
            c.ArcTo(new Point(0, h - r), new Size(r, r), 0, false, SweepDirection.Clockwise, true, true);
            c.LineTo(new Point(0, r), true, true);
            c.ArcTo(new Point(r, 0), new Size(r, r), 0, false, SweepDirection.Clockwise, true, true);
        }
        geo.Freeze();
        root.Children.Add(new Path
        {
            Data = geo, Fill = Solid(a.Style.Fill.A > 0 ? a.Style.Fill : Colors.White),
            Stroke = a.Style.Width > 0 ? Solid(a.Style.Outline) : null, StrokeThickness = a.Style.Width,
            StrokeLineJoin = PenLineJoin.Round,
        });
        var textArea = new Grid { Width = w, Height = h };
        textArea.Children.Add(new TextBlock
        {
            Text = a.Text, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
            Foreground = Solid(Contrast(a.Style.Fill.A > 0 ? a.Style.Fill : Colors.White)),
            FontSize = a.Style.FontSize, FontFamily = new FontFamily("Segoe UI"), FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 6, 10, 6),
        });
        root.Children.Add(textArea);
        return root;
    }

    private static TextBlock BuildText(Annotation a)
    {
        var t = new TextBlock
        {
            Text = a.Text, Foreground = Solid(a.Style.Outline),
            FontSize = a.Style.FontSize, FontFamily = new FontFamily("Segoe UI"), FontWeight = FontWeights.SemiBold,
        };
        Canvas.SetLeft(t, a.A.X);
        Canvas.SetTop(t, a.A.Y);
        return t;
    }

    /// <summary>Default badge side for a step of the given font size.</summary>
    public static double StepSize(double fontSize) => fontSize * 1.7 + 6;

    /// <summary>A step badge box of <paramref name="side"/> pixels centred on <paramref name="center"/>.</summary>
    public static (Point A, Point B) StepBox(Point center, double side) =>
        (center - new Vector(side / 2, side / 2), center + new Vector(side / 2, side / 2));

    /// <summary>The badge is a square: its side follows the larger dimension of the A–B box (so corner handles resize it).</summary>
    private static Grid BuildStep(Annotation a)
    {
        var box = Normalize(a.A, a.B);
        double size = Math.Max(12, Math.Max(box.Width, box.Height));
        var grid = new Grid { Width = size, Height = size };
        Shape badge = a.Style.StepShape == StepShape.Square
            ? new Rectangle { RadiusX = size * 0.15, RadiusY = size * 0.15 }
            : new Ellipse();
        badge.Fill = Solid(a.Style.Fill);
        badge.Stroke = Solid(a.Style.Outline);
        badge.StrokeThickness = a.Style.Width;
        grid.Children.Add(badge);
        string label = a.StepLabel;
        grid.Children.Add(new TextBlock
        {
            Text = label, Foreground = Solid(Contrast(a.Style.Fill)), FontWeight = FontWeights.Bold,
            // Text scales with the badge; longer labels shrink to stay inside.
            FontSize = Math.Max(6, (size - 6) / 1.7 * (label.Length > 2 ? 2.0 / label.Length : 1)),
            FontFamily = new FontFamily("Segoe UI"),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        });
        Canvas.SetLeft(grid, box.X);
        Canvas.SetTop(grid, box.Y);
        return grid;
    }

    private static Image BuildBlur(Annotation a)
    {
        var image = new Image { Source = a.Pixels, Stretch = Stretch.Fill };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);
        Place(image, Normalize(a.A, a.B));
        return image;
    }

    private static Image BuildPicture(Annotation a)
    {
        var image = new Image { Source = a.Pixels, Stretch = Stretch.Fill };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        Place(image, Normalize(a.A, a.B));
        return image;
    }

    /// <summary>Pixelates a region of the base image. The tiny bitmap is copied out so it doesn't pin the base image.</summary>
    public static BitmapSource Pixelate(BitmapSource baseImage, Int32Rect region)
    {
        region = ScreenCapture.Clamp(region, baseImage.PixelWidth, baseImage.PixelHeight);
        // Small regions get smaller blocks so the scaled-down bitmap never collapses to zero pixels.
        int block = Math.Clamp(Math.Min(region.Width, region.Height) / 8, 2, 16);
        var small = new TransformedBitmap(new CroppedBitmap(baseImage, region), new ScaleTransform(1.0 / block, 1.0 / block));
        var copy = new WriteableBitmap(small);
        copy.Freeze();
        return copy;
    }

    // ------------------------------------------------------------------ gallery previews

    /// <summary>A small sample of a preset for the Quick Styles gallery, drawn with the real builders.</summary>
    public static FrameworkElement Preview(Tool tool, AnnotationStyle s)
    {
        var canvas = new Canvas { Width = 60, Height = 40 };
        Annotation sample = tool switch
        {
            Tool.Arrow => new() { Kind = Tool.Arrow, A = new(8, 32), B = new(52, 9), Style = s with { Width = Math.Min(s.Width, 6) } },
            Tool.Line => new() { Kind = Tool.Line, A = new(8, 32), B = new(52, 9), Style = s with { Width = Math.Min(s.Width, 6) } },
            Tool.Pen => new()
            {
                Kind = Tool.Pen, Style = s,
                Points = Enumerable.Range(0, 24).Select(i => new Point(8 + i * 2, 20 + Math.Sin(i / 3.0) * 9)).ToList(),
            },
            Tool.Rectangle => new() { Kind = Tool.Rectangle, A = new(10, 7), B = new(50, 33), Style = s with { Width = Math.Min(s.Width, 4) } },
            Tool.Ellipse => new() { Kind = Tool.Ellipse, A = new(10, 7), B = new(50, 33), Style = s with { Width = Math.Min(s.Width, 4) } },
            Tool.Highlight => new() { Kind = Tool.Highlight, A = new(6, 13), B = new(54, 27), Style = s },
            Tool.Text => new() { Kind = Tool.Text, A = new(10, 4), Text = "Aa", Style = s with { FontSize = 24 } },
            Tool.Callout => new() { Kind = Tool.Callout, A = new(6, 3), B = new(54, 26), Text = "Aa", Style = s with { FontSize = 11, Width = Math.Min(s.Width, 2) } },
            Tool.Step => new() { Kind = Tool.Step, A = new(16, 6), B = new(44, 34), Number = 1, Style = s },
            _ => new() { Kind = Tool.Rectangle, A = new(10, 7), B = new(50, 33), Style = s },
        };
        canvas.Children.Add(Build(sample));
        canvas.ClipToBounds = true;
        canvas.IsHitTestVisible = false;
        return canvas;
    }

    // ------------------------------------------------------------------ helpers

    public static void Place(FrameworkElement e, Rect r)
    {
        Canvas.SetLeft(e, r.X);
        Canvas.SetTop(e, r.Y);
        e.Width = Math.Max(1, r.Width);
        e.Height = Math.Max(1, r.Height);
    }

    public static Rect Normalize(Point a, Point b) =>
        new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
}
