using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SnagitAnything.Editor;

namespace SnagitAnything.Services;

public static class ClipboardHelper
{
    /// <summary>
    /// Puts an image on the clipboard in two formats, so it pastes everywhere:
    /// a 24-bit bitmap on white (Paint, LINE, Teams, older apps — 32-bit alpha often pastes as black)
    /// and a "PNG" stream that keeps transparency (Word / PowerPoint / browsers prefer it).
    /// Retries briefly because another process may be holding the clipboard open.
    /// </summary>
    public static void SetImage(BitmapSource image)
    {
        var opaque = new FormatConvertedBitmap(ImageOps.OnWhite(image), PixelFormats.Bgr24, null, 0);
        opaque.Freeze();

        var png = new MemoryStream();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        encoder.Save(png);
        png.Position = 0;

        var data = new DataObject();
        data.SetImage(opaque);
        data.SetData("PNG", png, autoConvert: false);

        for (int attempt = 0; ; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(data, copy: true);
                return;
            }
            catch (COMException) when (attempt < 5)
            {
                Thread.Sleep(60);
            }
        }
    }

    /// <summary>Reads an image from the clipboard (PNG first, to keep transparency), or null.</summary>
    public static BitmapSource? GetImage()
    {
        try
        {
            if (Clipboard.GetData("PNG") is MemoryStream png)
            {
                var decoder = new PngBitmapDecoder(png, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                var frame = decoder.Frames[0];
                return Capture.ScreenCapture.Materialize(frame, new Int32Rect(0, 0, frame.PixelWidth, frame.PixelHeight), forceOpaque: false);
            }
            if (Clipboard.ContainsImage() && Clipboard.GetImage() is { } img)
                return Capture.ScreenCapture.Materialize(img, new Int32Rect(0, 0, img.PixelWidth, img.PixelHeight)); // DIB alpha is unreliable
        }
        catch (Exception ex)
        {
            Log.Error("Reading clipboard image failed", ex);
        }
        return null;
    }
}
