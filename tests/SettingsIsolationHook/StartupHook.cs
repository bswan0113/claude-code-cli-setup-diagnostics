using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

public static class StartupHook
{
    public static void Initialize()
    {
        var root = Environment.GetEnvironmentVariable("BLSLAB_QA_SETTINGS_ROOT");
        if (!Path.IsPathFullyQualified(root ?? "") || !Directory.Exists(root))
            throw new InvalidOperationException("The isolated QA settings directory was not provided.");

        var app = Assembly.GetEntryAssembly() ?? throw new InvalidOperationException("The launcher assembly was not found.");
        Redirect(app, "ClaudeLauncher.AppearancePreferences", "Store", Path.Combine(root!, "appearance.json"));
        Redirect(app, "ClaudeLauncher.UiLanguage", "PreferencePath", Path.Combine(root!, "language.txt"));
        Redirect(app, "ClaudeLauncher.SessionRecency", "Store", Path.Combine(root!, "session-launches"));
        File.WriteAllText(Path.Combine(root!, "isolated.ready"), "Appearance, language, and recency paths verified.");
        var timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle) { Interval = TimeSpan.FromMilliseconds(100) };
        timer.Tick += (_, _) => CaptureRequestedFrame(root!);
        timer.Start();
    }

    private static void CaptureRequestedFrame(string root)
    {
        var request = Path.Combine(root, "capture.request");
        if (!File.Exists(request) || Application.Current?.MainWindow is not { Content: FrameworkElement content } ||
            content.ActualWidth <= 0 || content.ActualHeight <= 0)
            return;

        var temp = Path.Combine(root, "frame.tmp");
        try
        {
            var dpi = VisualTreeHelper.GetDpi(content);
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth * dpi.DpiScaleX),
                (int)Math.Ceiling(content.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            bitmap.Render(content);
            var png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(bitmap));
            using (var file = File.Create(temp))
                png.Save(file);
            File.Move(temp, Path.Combine(root, "frame.png"), overwrite: true);
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or ArgumentException)
        {
            File.WriteAllText(Path.Combine(root, "frame.error"), error.GetType().Name);
        }
        finally { File.Delete(request); }
    }

    public static void Redirect(Assembly app, string typeName, string fieldName, string path)
    {
        var type = app.GetType(typeName, throwOnError: true)!;
        var field = type.GetField(fieldName, BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(typeName, fieldName);
        _ = field.GetValue(null);

        // Scope the redirection to the fixture process before the launcher reads preferences.
        var method = new DynamicMethod("UseIsolatedSettingsFile", typeof(void), [typeof(string)], type, skipVisibility: true);
        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Stsfld, field);
        il.Emit(OpCodes.Ret);
        method.CreateDelegate<Action<string>>()(path);
        if (!string.Equals(field.GetValue(null) as string, path, StringComparison.Ordinal))
            throw new InvalidOperationException($"Could not isolate launcher settings field {fieldName}.");
    }
}
