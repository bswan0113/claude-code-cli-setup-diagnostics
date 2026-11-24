using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal static partial class Program
{
    private static void TryCaptureWindow(IntPtr window, string? directory, string filename, string? settings = null)
    {
        try
        {
            CaptureWindow(window, directory, filename, settings);
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or ExternalException)
        {
            Console.WriteLine($"CAPTURE BLOCKED {filename} | {error.Message}");
        }
    }

    private static void CaptureWindow(IntPtr window, string? directory, string filename, string? settings = null)
    {
        if (directory is null)
            return;
        if (settings is not null)
        {
            CaptureFixtureClient(settings, directory, filename);
            return;
        }
        CaptureNativeFrame(window, directory, filename);
    }

    private static void CaptureFixtureClient(string settings, string directory, string filename)
    {
        var frame = Path.Combine(settings, "frame.png");
        var error = Path.Combine(settings, "frame.error");
        File.Delete(frame);
        File.Delete(error);
        File.WriteAllText(Path.Combine(settings, "capture.request"), "capture");
        WaitFor("The isolated fixture did not render its own window.", () => File.Exists(frame) || File.Exists(error));
        Require(!File.Exists(error), $"The isolated fixture could not render its own window: {(File.Exists(error) ? File.ReadAllText(error) : "") }.");
        Directory.CreateDirectory(directory);
        File.Copy(frame, Path.Combine(directory, filename), overwrite: true);
    }

    private static void CaptureNativeFrame(IntPtr window, string directory, string filename)
    {

        Require(GetWindowRect(window, out var bounds), "Could not measure the fixture window for capture.");
        var screen = GetDC(IntPtr.Zero);
        try
        {
            var target = CreateCompatibleDC(screen);
            var bitmap = CreateCompatibleBitmap(screen, bounds.Width, bounds.Height);
            var previous = SelectObject(target, bitmap);
            try
            {
                Require(PrintWindow(window, target, 2), "Windows could not capture the fixture window.");
                var source = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero,
                    Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                var pixels = new byte[bounds.Width * 4];
                var normalized = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
                var colors = new HashSet<int>();
                for (var part = 1; part < 10 && colors.Count < 3; part++)
                {
                    normalized.CopyPixels(new Int32Rect(0, bounds.Height * part / 10, bounds.Width, 1), pixels, pixels.Length, 0);
                    for (var i = 0; i < pixels.Length && colors.Count < 3; i += 4)
                        colors.Add(pixels[i] | pixels[i + 1] << 8 | pixels[i + 2] << 16);
                }
                Require(colors.Count >= 3, "The fixture window capture was blank.");
                Directory.CreateDirectory(directory);
                using var file = File.Create(Path.Combine(directory, filename));
                var png = new PngBitmapEncoder();
                png.Frames.Add(BitmapFrame.Create(source));
                png.Save(file);
            }
            finally
            {
                SelectObject(target, previous);
                DeleteObject(bitmap);
                DeleteDC(target);
            }
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    private static (int LeftGap, int RightGap, double WidthDip) MeasureDock(IntPtr window)
    {
        Require(GetWindowRect(window, out var bounds), "Could not read the sidebar bounds.");
        var desktop = ReadDesktopBounds(window);
        return (bounds.Left - desktop.Work.Left, desktop.Work.Right - bounds.Right, bounds.Width * 96d / GetDpiForWindow(window));
    }

    private static (WindowRect Monitor, WindowRect Work) ReadDesktopBounds(IntPtr window)
    {
        var monitor = MonitorFromWindow(window, 2);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        Require(GetMonitorInfo(monitor, ref info), "Could not read the sidebar monitor work area.");
        return (info.Monitor, info.Work);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowRect
    {
        public int Left, Top, Right, Bottom;
        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public WindowRect Monitor, Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out WindowRect bounds);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr window);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr window, IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr source);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr dc, IntPtr bitmap);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr handle);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr dc);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PrintWindow(IntPtr window, IntPtr dc, uint flags);
}
