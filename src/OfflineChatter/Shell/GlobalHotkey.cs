using System;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using OfflineChatter.Core.Diagnostics;

namespace OfflineChatter.Shell;

/// <summary>Системная горячая клавиша (RegisterHotKey, Win7+). Срабатывает, даже когда окно свёрнуто.</summary>
public sealed class GlobalHotkey : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const int Id = 0x4F43; // "OC"
    private readonly HwndSource _source;
    private readonly Action _action;
    private bool _registered;

    public GlobalHotkey(HwndSource source, ModifierKeys modifiers, Key key, Action action)
    {
        _source = source;
        _action = action;
        uint mods = 0x4000; // MOD_NOREPEAT (Win7+)
        if (modifiers.HasFlag(ModifierKeys.Alt)) mods |= 0x1;
        if (modifiers.HasFlag(ModifierKeys.Control)) mods |= 0x2;
        if (modifiers.HasFlag(ModifierKeys.Shift)) mods |= 0x4;
        if (modifiers.HasFlag(ModifierKeys.Windows)) mods |= 0x8;
        _registered = RegisterHotKey(source.Handle, Id, mods, (uint)KeyInterop.VirtualKeyFromKey(key));
        if (_registered) source.AddHook(Hook);
        else Log.Warn("App", "Global hotkey is taken by another program", null, new { modifiers = modifiers.ToString(), key = key.ToString() });
    }

    public bool IsRegistered => _registered;

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt32() == Id)
        {
            _action();
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (!_registered) return;
        UnregisterHotKey(_source.Handle, Id);
        _source.RemoveHook(Hook);
        _registered = false;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
