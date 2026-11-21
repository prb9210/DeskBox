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

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int SetWindowRgn(
        IntPtr hWnd,
        IntPtr hRgn,
        [MarshalAs(UnmanagedType.Bool)] bool redraw);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeleteObject(IntPtr handle);
}
