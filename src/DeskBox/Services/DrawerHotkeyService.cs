using System;
using DeskBox.Platform;

namespace DeskBox.Services;

/// <summary>
/// Ctrl+Alt+Space to collapse / raise the whole widget layer.
///
/// Deliberately a second, tiny service instead of a second
/// <see cref="GlobalHotkeyService"/> instance: that one writes the gesture into
/// settings, so pointing it at a drawer gesture would silently overwrite the
/// user's own activation hotkey. This one owns a fixed gesture, never touches
/// settings, and degrades to "no hotkey" without disturbing the ball or tray.
/// </summary>
internal sealed class DrawerHotkeyService : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const int HotkeyId = 0x0D5B;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModNoRepeat = 0x4000;
    private const uint VirtualKeySpace = 0x20;

    private static readonly UIntPtr SubclassId = new(0x0D5B);

    private readonly Action _onPressed;
    private readonly Win32Helper.SubclassProc _subclassProc;

    private IntPtr _windowHandle = IntPtr.Zero;
    private bool _subclassInstalled;
    private bool _registered;

    public DrawerHotkeyService(Action onPressed)
    {
        _onPressed = onPressed;
        _subclassProc = CallbackDepth.WrapSubclassProc(WindowSubclassProc);
    }

    public bool IsRegistered => _registered;

    /// <summary>Attaches to the drawer window and registers the chord.</summary>
    public bool TryRegister(IntPtr windowHandle, out string? error)
    {
        error = null;
        if (windowHandle == IntPtr.Zero)
        {
            error = "窗口句柄无效";
            return false;
        }

        if (_registered && _windowHandle == windowHandle)
        {
            return true;
        }

        Detach();

        _windowHandle = windowHandle;
        try
        {
            _subclassInstalled = Win32Helper.SetWindowSubclass(
                _windowHandle, _subclassProc, SubclassId, UIntPtr.Zero);
        }
        catch (Exception ex)
        {
            App.Log($"[DrawerHotkey] subclass failed: {ex.Message}");
        }

        uint modifiers = ModControl | ModAlt | ModNoRepeat;
        try
        {
            _registered = Win32Helper.RegisterHotKey(
                _windowHandle, HotkeyId, modifiers, VirtualKeySpace);
        }
        catch (Exception ex)
        {
            App.Log($"[DrawerHotkey] RegisterHotKey threw: {ex.Message}");
            _registered = false;
        }

        if (!_registered)
        {
            // Already taken by another app (or an earlier instance of DeskBox):
            // the ball and the tray still work, so this is a soft failure.
            error = "Ctrl+Alt+空格 已被其他程序占用";
            App.Log($"[DrawerHotkey] Ctrl+Alt+Space unavailable: {error}");
            return false;
        }

        App.Log("[DrawerHotkey] registered Ctrl+Alt+Space");
        return true;
    }

    public void Detach()
    {
        if (_windowHandle == IntPtr.Zero)
        {
            return;
        }

        try
        {
            if (_registered)
            {
                Win32Helper.UnregisterHotKey(_windowHandle, HotkeyId);
            }
        }
        catch (Exception ex)
        {
            App.Log($"[DrawerHotkey] UnregisterHotKey failed: {ex.Message}");
        }

        if (_subclassInstalled)
        {
            try
            {
                Win32Helper.RemoveWindowSubclass(_windowHandle, _subclassProc, SubclassId);
            }
            catch (Exception ex)
            {
                App.Log($"[DrawerHotkey] RemoveWindowSubclass failed: {ex.Message}");
            }
        }

        _registered = false;
        _subclassInstalled = false;
        _windowHandle = IntPtr.Zero;
    }

    public void Dispose() => Detach();

    private IntPtr WindowSubclassProc(
        IntPtr hWnd,
        uint msg,
        UIntPtr wParam,
        IntPtr lParam,
        UIntPtr uIdSubclass,
        UIntPtr dwRefData)
    {
        if (msg == WmHotkey && wParam.ToUInt64() == HotkeyId)
        {
            try
            {
                _onPressed();
            }
            catch (Exception ex)
            {
                App.Log($"[DrawerHotkey] handler failed: {ex.Message}");
            }

            return IntPtr.Zero;
        }

        return Win32Helper.DefSubclassProc(hWnd, msg, wParam, lParam);
    }
}
