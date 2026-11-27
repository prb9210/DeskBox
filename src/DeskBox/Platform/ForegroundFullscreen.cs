using System;
using System.Runtime.InteropServices;
using System.Text;

namespace DeskBox.Platform;

/// <summary>
/// 「全屏时自动隐藏悬浮球」的判据。只看前台窗口是否完整覆盖它所在的显示器，
/// 并排除桌面/任务栏这类本身就是全屏的系统窗口——否则球会在桌面上永远不出现。
/// </summary>
public static class ForegroundFullscreen
{
    private const uint MonitorDefaultToNearest = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO info);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder name, int count);

    public static bool IsForegroundFullscreen()
    {
        try
        {
            IntPtr hWnd = GetForegroundWindow();
            if (hWnd == IntPtr.Zero)
            {
                return false;
            }

            var className = new StringBuilder(64);
            GetClassName(hWnd, className, className.Capacity);
            string name = className.ToString();
            if (name is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
            {
                return false;
            }

            if (!GetWindowRect(hWnd, out RECT window))
            {
                return false;
            }

            IntPtr monitor = MonitorFromWindow(hWnd, MonitorDefaultToNearest);
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(monitor, ref info))
            {
                return false;
            }

            RECT screen = info.rcMonitor;
            return window.Left <= screen.Left &&
                   window.Top <= screen.Top &&
                   window.Right >= screen.Right &&
                   window.Bottom >= screen.Bottom;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
