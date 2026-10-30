using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Media.Imaging;

namespace SnagitAnything.Services;

/// <summary>Saved captures on disk, newest first (Snagit's "recent captures" tray).</summary>
public sealed class CaptureLibrary
{
    private static readonly string[] Extensions = { ".png", ".jpg", ".jpeg", ".bmp", ".gif" };
    private readonly AppSettings _settings;

    /// <summary>
    /// Used when the chosen folder can't be written — typically Windows Defender's Controlled Folder Access,
    /// which blocks unknown apps from Pictures/Documents/Desktop and makes CreateDirectory fail.
    /// </summary>
    public static string FallbackFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SnagitAnything", "Captures");

    public CaptureLibrary(AppSettings settings) => _settings = settings;

    /// <summary>Set once a capture had to go to <see cref="FallbackFolder"/>.</summary>
    public bool UsingFallback { get; private set; }

    /// <summary>Writes a new capture, falling back to a folder we can always write. Returns the path used.</summary>
    public string SaveNew(BitmapSource image)
    {
        string name = $"capture_{DateTime.Now:HHmmss_fff}.{_settings.ImageFormat.TrimStart('.')}";
        string day = DateTime.Now.ToString("yyyy-MM-dd");
        try
        {
            string path = Path.Combine(_settings.SaveFolder, day, name);
            Save(image, path);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error($"Cannot write to {_settings.SaveFolder}; using {FallbackFolder}", ex);
            UsingFallback = true;
            string path = Path.Combine(FallbackFolder, day, name);
            Save(image, path);
            return path;
        }
    }

    public IReadOnlyList<FileInfo> Recent(int max = 60)
    {
        return new[] { _settings.SaveFolder, FallbackFolder }
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(Directory.Exists)
            .SelectMany(d => new DirectoryInfo(d).EnumerateFiles("*", SearchOption.AllDirectories))
            .Where(f => Extensions.Contains(f.Extension, StringComparer.OrdinalIgnoreCase))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Take(max)
            .ToList();
    }

    public static void Save(BitmapSource image, string path)
    {
        BitmapEncoder encoder = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => new JpegBitmapEncoder { QualityLevel = 92 },
            ".bmp" => new BmpBitmapEncoder(),
            ".gif" => new GifBitmapEncoder(),
            _ => new PngBitmapEncoder(),
        };
        // JPEG/BMP have no alpha channel; put transparent areas on white rather than black.
        BitmapSource frame = encoder is PngBitmapEncoder ? image
            : new FormatConvertedBitmap(Editor.ImageOps.OnWhite(image), System.Windows.Media.PixelFormats.Bgr24, null, 0);
        encoder.Frames.Add(BitmapFrame.Create(frame));

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + ".tmp";
        using (var stream = File.Create(temp)) encoder.Save(stream);
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Loads a file fully into memory (no file lock, no lingering stream). Optional decode width for thumbnails.</summary>
    public static BitmapImage Load(string path, int decodeWidth = 0)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
        if (decodeWidth > 0) image.DecodePixelWidth = decodeWidth;
        image.UriSource = new Uri(path);
        image.EndInit();
        image.Freeze();
        return image;
    }
}
