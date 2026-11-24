using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ClaudeLauncher;

internal static class DesktopSidebar
{
    public static bool MoveToEdge(Window window, bool right, double widthDip,
        string? preferredMonitor, double? heightDip, double insetDip, double topDip)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == 0 || !double.IsFinite(widthDip) || widthDip <= 0 ||
            heightDip is double h && (!double.IsFinite(h) || h <= 0))
            return false;

        var current = MonitorFromWindow(hwnd, 2);
        if (!TryReadMonitor(current, out var monitor)) return false;
        var target = current;
        if (!string.IsNullOrEmpty(preferredMonitor) &&
            !string.Equals(monitor.Device, preferredMonitor, StringComparison.OrdinalIgnoreCase))
        {
            EnumDisplayMonitors(0, 0, (candidate, _, _, _) =>
            {
                if (!TryReadMonitor(candidate, out var info) ||
                    !string.Equals(info.Device, preferredMonitor, StringComparison.OrdinalIgnoreCase))
                    return true;
                target = candidate;
                monitor = info;
                return false;
            }, 0);
        }

        var dpi = GetDpiForWindow(hwnd);
        if (target != current && GetAwarenessFromDpiAwarenessContext(GetWindowDpiAwarenessContext(hwnd)) == 2 &&
            GetScaleFactorForMonitor(target, out var percent) == 0 && percent > 0)
            dpi = (uint)Math.Round(96.0 * percent / 100.0);
        if (!Place(hwnd, monitor.Work, dpi, right, widthDip, heightDip, insetDip, topDip))
            return false;
        if (target == current) return true;

        // Moving between displays can deliver WM_DPICHANGED and let WPF resize the HWND.
        // Read its destination DPI after the move and anchor the final rectangle to that work area.
        var destination = MonitorFromWindow(hwnd, 2);
        if (destination == target || !TryReadMonitor(target, out monitor))
            return TryReadMonitor(destination, out monitor) &&
                Place(hwnd, monitor.Work, GetDpiForWindow(hwnd), right, widthDip, heightDip, insetDip, topDip);
        return Place(hwnd, monitor.Work, dpi, right, widthDip, heightDip, insetDip, topDip);
    }

    public static bool TryReadPlacement(Window window, bool right, out Size size, out Point offset, out bool fillsHeight)
    {
        size = default;
        offset = default;
        fillsHeight = false;
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == 0 || !GetWindowRect(hwnd, out var bounds) ||
            !TryReadMonitor(MonitorFromWindow(hwnd, 2), out var monitor)) return false;
        var dpi = GetDpiForWindow(hwnd);
        if (dpi == 0 || bounds.Right <= bounds.Left || bounds.Bottom <= bounds.Top) return false;
        double scale = 96d / dpi;
        var work = monitor.Work;
        size = new Size((bounds.Right - bounds.Left) * scale, (bounds.Bottom - bounds.Top) * scale);
        offset = new Point(Math.Max(0, right ? work.Right - bounds.Right : bounds.Left - work.Left) * scale,
            Math.Max(0, bounds.Top - work.Top) * scale);
        fillsHeight = bounds.Top == work.Top && bounds.Bottom == work.Bottom;
        return true;
    }

    public static string? MonitorName(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        return hwnd != 0 && TryReadMonitor(MonitorFromWindow(hwnd, 2), out var info) ? info.Device : null;
    }

    public static Size WorkSize(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        var dpi = hwnd == 0 ? 0u : GetDpiForWindow(hwnd);
        if (dpi > 0 && TryReadMonitor(MonitorFromWindow(hwnd, 2), out var info))
            return new Size((info.Work.Right - info.Work.Left) * 96.0 / dpi, (info.Work.Bottom - info.Work.Top) * 96.0 / dpi);
        return SystemParameters.WorkArea.Size;
    }

    private static bool TryReadMonitor(nint handle, out MonitorInfo info)
    {
        info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        return handle != 0 && GetMonitorInfo(handle, ref info) &&
            info.Work.Right > info.Work.Left && info.Work.Bottom > info.Work.Top;
    }

    private static bool Place(nint hwnd, NativeRect work, uint dpi, bool right, double widthDip, double? heightDip,
        double insetDip, double topDip)
    {
        if (dpi == 0) return false;
        var width = (int)Math.Clamp(Math.Round(widthDip * dpi / 96.0), 1, work.Right - work.Left);
        var height = heightDip is double h
            ? (int)Math.Clamp(Math.Round(h * dpi / 96.0), 1, work.Bottom - work.Top)
            : work.Bottom - work.Top;
        var inset = (int)Math.Clamp(Math.Round(insetDip * dpi / 96d), 0, work.Right - work.Left - width);
        var x = right ? work.Right - width - inset : work.Left + inset;
        var y = work.Top + (int)Math.Clamp(Math.Round(topDip * dpi / 96d), 0, work.Bottom - work.Top - height);
        const uint noSize = 0x0001, noZOrder = 0x0004, noActivate = 0x0010;
        var flags = noZOrder | noActivate;
        if (GetWindowRect(hwnd, out var bounds) && bounds.Right - bounds.Left == width && bounds.Bottom - bounds.Top == height)
        {
            if (bounds.Left == x && bounds.Top == y)
                return true;
            flags |= noSize;
        }
        return SetWindowPos(hwnd, 0, x, y, width, height, flags);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public uint Size;
        public NativeRect Bounds, Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string? Device;
    }
    private delegate bool MonitorCallback(nint monitor, nint dc, nint rect, nint data);
    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(nint dc, nint clip, MonitorCallback callback, nint data);
    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint hwnd, out NativeRect bounds);
    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")]
    private static extern nint GetWindowDpiAwarenessContext(nint hwnd);
    [DllImport("user32.dll")]
    private static extern int GetAwarenessFromDpiAwarenessContext(nint context);
    [DllImport("shcore.dll")]
    private static extern int GetScaleFactorForMonitor(nint monitor, out int scalePercent);
    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int w, int h, uint flags);
}
