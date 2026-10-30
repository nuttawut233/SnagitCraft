using System;
using System.Collections.Generic;
using System.Windows.Interop;
using static SnagitAnything.Native.NativeMethods;

namespace SnagitAnything.Services;

public sealed record Hotkey(string Name, uint Modifiers, uint Key, string Display);

/// <summary>System-wide hotkeys via RegisterHotKey on a message-only window.</summary>
public sealed class HotkeyManager : IDisposable
{
    private readonly HwndSource _source;
    private readonly Dictionary<int, Action> _actions = new();
    private int _nextId = 0x5A00;

    public HotkeyManager()
    {
        var p = new HwndSourceParameters("SnagitAnythingHotkeys") { ParentWindow = new IntPtr(-3) /* HWND_MESSAGE */ };
        _source = new HwndSource(p);
        _source.AddHook(WndProc);
    }

    /// <summary>Returns false when another program already owns the combination (e.g. Windows' own Print Screen tool).</summary>
    public bool TryRegister(Hotkey hotkey, Action action)
    {
        int id = _nextId++;
        if (!RegisterHotKey(_source.Handle, id, hotkey.Modifiers | MOD_NOREPEAT, hotkey.Key))
        {
            Log.Info($"Hotkey {hotkey.Display} is taken by another program");
            return false;
        }
        _actions[id] = action;
        return true;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && _actions.TryGetValue(wParam.ToInt32(), out var action))
        {
            handled = true;
            action();
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        foreach (int id in _actions.Keys) UnregisterHotKey(_source.Handle, id);
        _actions.Clear();
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }
}
