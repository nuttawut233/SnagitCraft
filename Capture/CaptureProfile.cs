using System.Collections.Generic;

namespace SnagitAnything.Capture;

/// <summary>What a capture does, end to end (Snagit's "capture profiles").</summary>
public enum CaptureProfile
{
    /// <summary>Region / window → save, clipboard, editor (per settings).</summary>
    Image,
    /// <summary>The whole monitor under the mouse.</summary>
    Monitor,
    /// <summary>All monitors as one image.</summary>
    FullScreen,
    /// <summary>Region / window → clipboard only (no file, no editor).</summary>
    Clipboard,
    /// <summary>Region / window after a 5-second countdown (to open menus first).</summary>
    Delayed,
}

public sealed record ProfileInfo(CaptureProfile Profile, string Name, string Icon, string DefaultHotkey, string Description);

public static class CaptureProfiles
{
    public const int DelayedSeconds = 5;

    public static readonly IReadOnlyList<ProfileInfo> All = new ProfileInfo[]
    {
        new(CaptureProfile.Image, "ภาพ (พื้นที่ / หน้าต่าง)", "⬚", "PrintScreen", "ลากเลือกพื้นที่ หรือคลิกหน้าต่าง แล้วเปิดหน้าแก้ไข"),
        new(CaptureProfile.Monitor, "จอที่เมาส์อยู่", "▭", "Shift+PrintScreen", "ทั้งจอที่ตัวชี้เมาส์อยู่"),
        new(CaptureProfile.FullScreen, "เต็มจอทุกจอ", "▣", "Ctrl+PrintScreen", "ทุกจอรวมเป็นภาพเดียว"),
        new(CaptureProfile.Clipboard, "ส่งไปคลิปบอร์ด", "⧉", "Ctrl+Shift+PrintScreen", "เลือกพื้นที่ แล้วคัดลอกเข้าคลิปบอร์ดเท่านั้น"),
        new(CaptureProfile.Delayed, $"หน่วงเวลา {DelayedSeconds} วินาที", "⏱", "", $"รอ {DelayedSeconds} วินาทีก่อนจับภาพ (เปิดเมนูไว้ก่อนได้)"),
    };

    public static ProfileInfo Get(CaptureProfile p) => All[(int)p];
}
