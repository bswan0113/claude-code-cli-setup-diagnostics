using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ClaudeLauncher;

internal static class DesktopSidebar
{
    public static bool MoveToEdge(Window window, bool right, double widthDip)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        var monitor = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (hwnd == 0 || !GetMonitorInfo(MonitorFromWindow(hwnd, 2), ref monitor))
            return false;

        var work = monitor.Work;
        var dpi = GetDpiForWindow(hwnd);
        if (dpi == 0)
            return false;
        var width = Math.Min((int)Math.Round(widthDip * dpi / 96.0), work.Right - work.Left);
        var x = right ? work.Right - width : work.Left;
        var height = work.Bottom - work.Top;
        const uint noSize = 0x0001, noZOrder = 0x0004, noActivate = 0x0010;
        var flags = noZOrder | noActivate;
        if (GetWindowRect(hwnd, out var bounds) && bounds.Right - bounds.Left == width && bounds.Bottom - bounds.Top == height)
        {
            if (bounds.Left == x && bounds.Top == work.Top)
                return true;
            flags |= noSize;
        }
        return SetWindowPos(hwnd, 0, x, work.Top, width, height, flags);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public uint Size; public NativeRect Bounds, Work; public uint Flags; }
    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint hwnd, out NativeRect bounds);
    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int w, int h, uint flags);
}
