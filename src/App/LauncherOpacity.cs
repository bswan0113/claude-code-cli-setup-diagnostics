using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ClaudeLauncher;

internal static class LauncherOpacity
{
    private const int ExtendedStyle = -20;
    private const int Layered = 0x00080000;
    private const int UseAlpha = 2;
    private const int StyleChanging = 0x007C;
    private static readonly DependencyProperty FilterInstalled = DependencyProperty.RegisterAttached(
        "FilterInstalled", typeof(bool), typeof(LauncherOpacity), new PropertyMetadata(false));

    // Keep the normal resizable WPF window and apply alpha to its native surface,
    // including the caption and any diagnostic overlay hosted inside it.
    public static void Apply(Window window, int opacityPercent)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == 0)
            return;

        var style = GetWindowLong(hwnd, ExtendedStyle);
        var percent = Math.Clamp(opacityPercent, 60, 100);
        var source = HwndSource.FromHwnd(hwnd);
        if (source is null)
            return;
        if (percent == 100)
        {
            if ((bool)window.GetValue(FilterInstalled))
            {
                source.RemoveHook(KeepUniformAlpha);
                window.ClearValue(FilterInstalled);
                SetLayeredWindowAttributes(hwnd, 0, 255, UseAlpha);
                SetWindowLong(hwnd, ExtendedStyle, style & ~Layered);
            }
            return;
        }
        if (!(bool)window.GetValue(FilterInstalled))
        {
            source.AddHook(KeepUniformAlpha);
            window.SetValue(FilterInstalled, true);
        }
        if ((style & Layered) == 0)
            SetWindowLong(hwnd, ExtendedStyle, style | Layered);
        SetLayeredWindowAttributes(hwnd, 0, (byte)((255 * percent + 50) / 100), UseAlpha);
    }

    private static nint KeepUniformAlpha(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        // HwndTarget otherwise strips the layered bit when WindowStyle changes on a normally
        // framed WPF window. Preserve only this bit; the caller's remaining styles still apply.
        if (message == StyleChanging && wParam == ExtendedStyle && lParam != 0)
        {
            const int newStyleOffset = sizeof(int);
            var proposed = Marshal.ReadInt32(lParam, newStyleOffset);
            Marshal.WriteInt32(lParam, newStyleOffset, proposed | Layered);
            handled = true;
        }
        return 0;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(nint window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(nint window, int index, int value);

    [DllImport("user32.dll")]
    private static extern bool SetLayeredWindowAttributes(nint window, uint colorKey, byte alpha, uint flags);
}
