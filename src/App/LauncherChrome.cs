using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ClaudeLauncher;

internal static class LauncherChrome
{
    public static void ApplyDarkCaption(Window window, bool darkMode)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == 0)
            return;

        int enabled = darkMode ? 1 : 0;
        const uint useImmersiveDarkMode = 20;
        DwmSetWindowAttribute(hwnd, useImmersiveDarkMode, ref enabled, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint window, uint attribute, ref int value, int size);
}
