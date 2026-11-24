using System.ComponentModel;
using System.Runtime.InteropServices;

namespace ClaudeLauncher;

internal static class MiniNative
{
    internal const uint Command = 0x0111, Close = 0x0010, Destroy = 2, SizeChanged = 5,
        DpiChanged = 0x02E0, GetMinMaxInfo = 0x0024, ControlColor = 0x0138, SetFont = 0x0030;
    internal const uint Changed = 0x8001, StartScan = 0x8002;
    internal const uint Child = 0x40000000, TabStop = 0x00010000, Visible = 0x10000000,
        Border = 0x00800000, VScroll = 0x00200000;
    internal const uint ComboAdd = 0x0143, ComboReset = 0x014B, ComboGet = 0x0147, ComboSelect = 0x014E;
    internal const int WindowColor = 5, WorkArea = 0x0030;
    private const string User = "user32.dll";
    internal delegate nint WindowProc(nint window, uint message, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct WindowClass
    {
        internal uint style;
        internal WindowProc procedure;
        internal int classExtra, windowExtra;
        internal nint instance, icon, cursor, background;
        internal string? menu;
        internal string name;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Point { internal int x, y; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect { internal int left, top, right, bottom; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct MinMax { internal Point reserved, maxSize, maxPos, minTrack, maxTrack; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Message
    {
        internal nint hwnd; internal uint id; internal nint wParam, lParam;
        internal uint time; internal Point point; internal uint priv;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] internal static extern nint GetModuleHandleW(string? name);
    [DllImport(User, CharSet = CharSet.Unicode, SetLastError = true)] internal static extern ushort RegisterClassW(ref WindowClass cls);
    [DllImport(User, CharSet = CharSet.Unicode, SetLastError = true)] internal static extern nint CreateWindowExW(uint ex, string kind, string text, uint style, int x, int y, int w, int h, nint parent, nint id, nint instance, nint data);
    [DllImport(User)] internal static extern nint LoadIconW(nint instance, nint resource);
    [DllImport(User)] internal static extern nint LoadCursorW(nint instance, nint resource);
    [DllImport(User)] internal static extern nint DefWindowProcW(nint window, uint msg, nint wParam, nint lParam);
    [DllImport(User, SetLastError = true)] internal static extern int GetMessageW(out Message msg, nint window, uint first, uint last);
    [DllImport(User)] internal static extern bool IsDialogMessageW(nint window, ref Message msg);
    [DllImport(User)] internal static extern bool TranslateMessage(ref Message msg);
    [DllImport(User)] internal static extern nint DispatchMessageW(ref Message msg);
    [DllImport(User)] internal static extern bool PostMessageW(nint window, uint msg, nint wParam, nint lParam);
    [DllImport(User)] internal static extern nint SendMessageW(nint window, uint msg, nint wParam, nint lParam);
    [DllImport(User, EntryPoint="SendMessageW", CharSet = CharSet.Unicode)] internal static extern nint SendText(nint window, uint msg, nint wParam, string text);
    [DllImport(User, CharSet = CharSet.Unicode)] internal static extern bool SetWindowTextW(nint window, string text);
    [DllImport(User)] internal static extern bool ShowWindow(nint window, int state);
    [DllImport(User)] internal static extern bool EnableWindow(nint window, bool enabled);
    [DllImport(User)] internal static extern bool DestroyWindow(nint window);
    [DllImport(User)] internal static extern void PostQuitMessage(int exit);
    [DllImport(User)] internal static extern bool MoveWindow(nint window, int x, int y, int w, int h, bool repaint);
    [DllImport(User)] internal static extern bool GetClientRect(nint window, out Rect rect);
    [DllImport(User)] internal static extern bool GetWindowRect(nint window, out Rect rect);
    [DllImport(User)] internal static extern uint GetDpiForWindow(nint window);
    [DllImport(User)] internal static extern uint GetDpiForSystem();
    [DllImport(User)] internal static extern bool SystemParametersInfoW(uint action, uint parameter, out Rect area, uint flags);
    [DllImport(User)] internal static extern nint GetSysColorBrush(int index);
    [DllImport(User)] internal static extern nint SetFocus(nint control);
    [DllImport(User)] internal static extern nint CreatePopupMenu();
    [DllImport(User)] internal static extern bool DestroyMenu(nint menu);
    [DllImport(User, CharSet = CharSet.Unicode)] internal static extern bool AppendMenuW(nint menu, uint flags, nuint id, string caption);
    [DllImport(User)] internal static extern int TrackPopupMenu(nint menu, uint flags, int x, int y, int reserved, nint window, nint rect);
    [DllImport(User, CharSet = CharSet.Unicode)] internal static extern int MessageBoxW(nint owner, string text, string title, uint type);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] internal static extern nint CreateFontW(int height, int width, int esc, int orient, int weight, uint italic, uint underline, uint strike, uint charset, uint output, uint clip, uint quality, uint pitch, string family);
    [DllImport("gdi32.dll")] internal static extern bool DeleteObject(nint obj);
    [DllImport("gdi32.dll")] internal static extern uint SetTextColor(nint dc, uint color);
    [DllImport("gdi32.dll")] internal static extern int SetBkMode(nint dc, int mode);
    [DllImport(User)] private static extern bool OpenClipboard(nint owner);
    [DllImport(User)] private static extern bool EmptyClipboard();
    [DllImport(User)] private static extern bool CloseClipboard();
    [DllImport(User)] private static extern nint SetClipboardData(uint format, nint memory);
    [DllImport("kernel32.dll")] private static extern nint GlobalAlloc(uint flags, nuint bytes);
    [DllImport("kernel32.dll")] private static extern nint GlobalLock(nint memory);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(nint memory);
    [DllImport("kernel32.dll")] private static extern nint GlobalFree(nint memory);

    internal static nint Checked(nint handle) => handle != 0 ? handle : throw new Win32Exception(Marshal.GetLastWin32Error());

    internal static bool Copy(nint owner, string text)
    {
        var chars = (text.ReplaceLineEndings("\r\n") + '\0').ToCharArray();
        var block = GlobalAlloc(2, checked((nuint)chars.Length * 2));
        if (block == 0) return false;
        try
        {
            var data = GlobalLock(block);
            if (data == 0) return false;
            try { Marshal.Copy(chars, 0, data, chars.Length); }
            finally { GlobalUnlock(block); }
            if (!OpenClipboard(owner)) return false;
            try
            {
                if (!EmptyClipboard() || SetClipboardData(13, block) == 0) return false;
                block = 0; // Windows now owns the movable block.
                return true;
            }
            finally { CloseClipboard(); }
        }
        finally { if (block != 0) GlobalFree(block); }
    }
}
