using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Input;
using System.Windows.Threading;

namespace SnagitAnything.Services;

/// <summary>A key plus modifiers, stored in settings as text such as "Ctrl+Shift+PrintScreen".</summary>
public sealed record HotkeyBinding(ModifierKeys Modifiers, Key Key)
{
    public int VirtualKey => KeyInterop.VirtualKeyFromKey(Key);

    public override string ToString()
    {
        var sb = new StringBuilder();
        if (Modifiers.HasFlag(ModifierKeys.Control)) sb.Append("Ctrl+");
        if (Modifiers.HasFlag(ModifierKeys.Alt)) sb.Append("Alt+");
        if (Modifiers.HasFlag(ModifierKeys.Shift)) sb.Append("Shift+");
        if (Modifiers.HasFlag(ModifierKeys.Windows)) sb.Append("Win+");
        sb.Append(Key == Key.Snapshot ? "PrintScreen" : Key.ToString());
        return sb.ToString();
    }

    /// <summary>Human-readable form for the UI.</summary>
    public string Display => ToString().Replace("PrintScreen", "Print Screen");

    public static HotkeyBinding? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var mods = ModifierKeys.None;
        Key? key = null;
        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl" or "control": mods |= ModifierKeys.Control; break;
                case "alt": mods |= ModifierKeys.Alt; break;
                case "shift": mods |= ModifierKeys.Shift; break;
                case "win" or "windows": mods |= ModifierKeys.Windows; break;
                case "printscreen" or "print screen" or "prtsc": key = Key.Snapshot; break;
                default:
                    if (Enum.TryParse<Key>(raw, ignoreCase: true, out var k)) key = k;
                    else return null;
                    break;
            }
        }
        return key is { } kk && kk != Key.None ? new HotkeyBinding(mods, kk) : null;
    }
}

/// <summary>
/// System-wide hotkeys through a low-level keyboard hook.
///
/// RegisterHotKey is not enough on Windows 11: with "Use the Print screen key to open screen capture"
/// on (the default), the shell takes Print Screen first and opens Snipping Tool, so a registered hotkey
/// never fires. Low-level hooks see keys before that handling and can swallow them.
///
/// The hook runs on its own thread with its own message loop: Windows silently removes a low-level hook
/// whose callback is slow, which would happen if it shared the UI thread during a capture.
/// The same hook also powers "press the key you want" recording in Settings, because the UI never
/// receives a Print Screen key-down of its own.
/// </summary>
public sealed class GlobalHotkeys : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105, WM_QUIT = 0x0012;
    private const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_LWIN = 0x5B, VK_RWIN = 0x5C, VK_ESCAPE = 0x1B;
    private static readonly HashSet<int> ModifierVks = new() { 0x10, 0x11, 0x12, 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0x5B, 0x5C };

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int id, HookProc fn, IntPtr hMod, uint threadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
    [DllImport("user32.dll")] private static extern int GetMessage(out MSG msg, IntPtr hWnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll")] private static extern IntPtr GetModuleHandle(string? name);

    private readonly Dispatcher _ui;
    private readonly HookProc _proc; // kept in a field so the GC never collects the callback
    private readonly Thread _thread;
    private IntPtr _hook;
    private uint _threadId;

    // Written on the UI thread, read on the hook thread: swapped atomically as whole objects.
    private volatile (HotkeyBinding Binding, Action Action)[] _bindings = Array.Empty<(HotkeyBinding, Action)>();
    private volatile Action<HotkeyBinding?>? _recorder;
    private int _swallowUpVk = -1;

    public GlobalHotkeys(Dispatcher ui)
    {
        _ui = ui;
        _proc = Callback;
        using var ready = new ManualResetEventSlim();
        _thread = new Thread(() => Run(ready)) { IsBackground = true, Name = "GlobalHotkeys" };
        _thread.Start();
        ready.Wait(TimeSpan.FromSeconds(5));
    }

    public bool IsInstalled => _hook != IntPtr.Zero;

    /// <summary>Replaces all hotkeys. Actions run on the UI thread.</summary>
    public void SetBindings(IEnumerable<(HotkeyBinding Binding, Action Action)> bindings) => _bindings = bindings.ToArray();

    /// <summary>The next key combination pressed anywhere is reported (and swallowed) instead of triggering a hotkey. Esc reports null.</summary>
    public void RecordNext(Action<HotkeyBinding?> onRecorded) => _recorder = onRecorded;

    public void CancelRecording() => _recorder = null;

    private void Run(ManualResetEventSlim ready)
    {
        _threadId = GetCurrentThreadId();
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero) Log.Error($"Keyboard hook failed, error {Marshal.GetLastWin32Error()}");
        ready.Set();
        while (GetMessage(out _, IntPtr.Zero, 0, 0) > 0) { }
        if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
    }

    private IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            int vk = (int)info.vkCode;
            int msg = wParam.ToInt32();

            if (msg is WM_KEYUP or WM_SYSKEYUP && vk == _swallowUpVk)
            {
                _swallowUpVk = -1;
                return new IntPtr(1);
            }

            if (msg is WM_KEYDOWN or WM_SYSKEYDOWN && !ModifierVks.Contains(vk))
            {
                if (vk == _swallowUpVk) return new IntPtr(1); // auto-repeat of a key we already handled

                var mods = CurrentModifiers();
                if (_recorder is { } record)
                {
                    _recorder = null;
                    _swallowUpVk = vk;
                    HotkeyBinding? recorded = vk == VK_ESCAPE && mods == ModifierKeys.None
                        ? null : new HotkeyBinding(mods, KeyInterop.KeyFromVirtualKey(vk));
                    _ui.BeginInvoke(() => record(recorded));
                    return new IntPtr(1);
                }

                foreach (var (binding, action) in _bindings)
                {
                    if (binding.VirtualKey == vk && binding.Modifiers == mods)
                    {
                        _swallowUpVk = vk;
                        _ui.BeginInvoke(action);
                        return new IntPtr(1);
                    }
                }
            }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private static ModifierKeys CurrentModifiers()
    {
        var m = ModifierKeys.None;
        if (IsDown(VK_CONTROL)) m |= ModifierKeys.Control;
        if (IsDown(VK_SHIFT)) m |= ModifierKeys.Shift;
        if (IsDown(VK_MENU)) m |= ModifierKeys.Alt;
        if (IsDown(VK_LWIN) || IsDown(VK_RWIN)) m |= ModifierKeys.Windows;
        return m;
    }

    private static bool IsDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    public void Dispose()
    {
        if (_threadId != 0) PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(TimeSpan.FromSeconds(2));
    }
}
