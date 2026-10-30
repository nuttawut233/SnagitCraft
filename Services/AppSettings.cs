using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using SnagitAnything.Capture;

namespace SnagitAnything.Services;

public enum DockEdge { Left, Top, Right, Bottom }

public sealed class AppSettings
{
    public string SaveFolder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "SnagitCraft");
    public bool CopyToClipboard { get; set; } = true;
    public bool OpenEditor { get; set; } = true;
    public bool AutoSave { get; set; } = true;
    public int DelaySeconds { get; set; }
    public string ImageFormat { get; set; } = "png";
    public bool StartMinimized { get; set; }

    /// <summary>Hotkey per capture profile, e.g. { "Image": "PrintScreen" }. Empty string = no hotkey.</summary>
    public Dictionary<string, string> Hotkeys { get; set; } = DefaultHotkeys();

    public bool ShowCaptureWidget { get; set; } = true;
    public CaptureProfile WidgetProfile { get; set; } = CaptureProfile.Image;
    /// <summary>Screen edge the capture widget is docked to (drag it to another edge to move it).</summary>
    public DockEdge WidgetEdge { get; set; } = DockEdge.Right;
    /// <summary>Position along that edge, 0 = top/left end, 1 = bottom/right end (centre of the widget).</summary>
    public double WidgetOffset { get; set; } = 0.3;
    /// <summary>A physical-pixel point on the monitor the widget is docked to; null = primary monitor.</summary>
    public int? WidgetMonitorX { get; set; }
    public int? WidgetMonitorY { get; set; }
    /// <summary>Tuck the widget into the edge (a thin tab) until the mouse comes near.</summary>
    public bool WidgetAutoHide { get; set; } = true;

    public static Dictionary<string, string> DefaultHotkeys() =>
        CaptureProfiles.All.ToDictionary(p => p.Profile.ToString(), p => p.DefaultHotkey);

    public HotkeyBinding? HotkeyFor(CaptureProfile p) =>
        HotkeyBinding.Parse(Hotkeys.TryGetValue(p.ToString(), out var s) ? s : CaptureProfiles.Get(p).DefaultHotkey);

    public static string ConfigFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SnagitAnything");

    private static string FilePath => Path.Combine(ConfigFolder, "settings.json");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            Log.Error("Could not read settings; using defaults", ex);
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(ConfigFolder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch (Exception ex)
        {
            Log.Error("Could not save settings", ex);
        }
    }
}
