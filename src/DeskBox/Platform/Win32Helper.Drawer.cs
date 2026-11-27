using System.Runtime.InteropServices;

namespace DeskBox.Platform;

/// <summary>
/// Drawer-specific interop: the exam countdown's end cue. Kept out of the UI
/// layer because P/Invoke is only allowed in the Platform domain.
/// </summary>
public static partial class Win32Helper
{
    /// <summary>0x40 = MB_ICONINFORMATION; blocking for well under a second.</summary>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool MessageBeep(uint type);
}
