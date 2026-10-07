using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace SpotlightWindows.Infrastructure.Windows;

/// <summary>
/// Handles window positioning, specifically centering the launcher
/// on the monitor where the mouse cursor is currently located.
/// Uses native Win32 APIs for accurate multi-monitor/DPI handling.
/// </summary>
public static class WindowPositionService
{
    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    // MONITOR_DEFAULTTONEAREST — returns nearest monitor if point isn't on any monitor
    private const uint MONITOR_DEFAULTTONEAREST = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork; // Working area (excludes taskbar)
        public uint dwFlags;
    }

    /// <summary>
    /// Centers the given window on the monitor where the mouse cursor is located.
    /// Uses the working area (excludes taskbar) for accurate positioning.
    /// Accounts for DPI scaling via the WPF window's DPI transform.
    /// </summary>
    public static void CenterOnCurrentMonitor(Window window)
    {
        if (!GetCursorPos(out POINT cursorPos))
            return;

        IntPtr monitor = MonitorFromPoint(cursorPos, MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero)
            return;

        var monitorInfo = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref monitorInfo))
            return;

        // Get the DPI scaling factor for this window
        var source = PresentationSource.FromVisual(window);
        double dpiScaleX = source?.CompositionTarget?.TransformFromDevice.M11 ?? 1.0;
        double dpiScaleY = source?.CompositionTarget?.TransformFromDevice.M22 ?? 1.0;

        // Convert physical pixels to WPF device-independent units
        var workArea = monitorInfo.rcWork;
        double workWidth = (workArea.Right - workArea.Left) * dpiScaleX;
        double workHeight = (workArea.Bottom - workArea.Top) * dpiScaleY;
        double workLeft = workArea.Left * dpiScaleX;
        double workTop = workArea.Top * dpiScaleY;

        // Center the window within the working area
        window.Left = workLeft + (workWidth - window.Width) / 2.0;
        window.Top = workTop + (workHeight - window.Height) / 3.0; // Slightly above center (1/3 from top)
    }
}
