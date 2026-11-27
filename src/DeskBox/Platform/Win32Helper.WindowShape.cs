using System;
using System.Runtime.InteropServices;

namespace DeskBox.Platform;

/// <summary>
/// Window-shape primitives. They live here because interop is only allowed in
/// the Platform domain (ModuleBoundaryContractTests): a circular desktop handle
/// needs an elliptic window region, not a rounded-corner square.
/// </summary>
public static partial class Win32Helper
{
    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern IntPtr CreateEllipticRgn(int left, int top, int right, int bottom);

    /// <summary>贴边条形态的窗口区域：圆角胶囊，不能再用椭圆（竖条会被裁成尖头）。</summary>
    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern IntPtr CreateRoundRectRgn(
        int left, int top, int right, int bottom, int widthEllipse, int heightEllipse);


    [DllImport("user32.dll", SetLastError = true)]
    public static extern int SetWindowRgn(
        IntPtr hWnd,
        IntPtr hRgn,
        [MarshalAs(UnmanagedType.Bool)] bool redraw);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeleteObject(IntPtr handle);
}
