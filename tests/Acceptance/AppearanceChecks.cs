using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Automation;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal static partial class Program
{
    private static string TestAppearanceWindow(string root, string release, string? captures)
    {
        VerifyFixtureOnlyProfile(recencyIsolated: true);
        var fixture = MakeDirectory(root, "appearance-ui");
        using var displayFolders = new DashboardFolderPaths(captures, fixture);
        var folder = MakeDirectory(displayFolders.Root, "QA appearance [fixture] & folder");
        var bin = CopyUnsignedCli(displayFolders.Root);
        var config = MakeDirectory(fixture, "claude-config");
        var project = MakeDirectory(config, Path.Combine("projects", "appearance-project"));
        var settings = MakeDirectory(fixture, "settings");
        var ready = Path.Combine(settings, "isolated.ready");
        var saved = Path.Combine(settings, "appearance.json");
        var language = Path.Combine(settings, "language.txt");
        var output = captures ?? MakeDirectory(fixture, "captures");
        var userSettings = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClaudeCodeSetupDiagnostics");
        var userAppearance = FileStamp(Path.Combine(userSettings, "appearance.json"));
        var userLanguage = FileStamp(Path.Combine(userSettings, "language.txt"));
        File.WriteAllText(language, "ko");
        foreach (var index in Enumerable.Range(1, 3))
        {
            var id = Guid.NewGuid();
            WriteSession(project, id, Older.AddMinutes(index), UserLine(id, folder, $"QA appearance session {index}", Older));
        }

        var exe = Path.Combine(MakeDirectory(fixture, "published"), "ClaudeLauncher.exe");
        File.Copy(release, exe);
        var start = new ProcessStartInfo(exe) { WorkingDirectory = folder, UseShellExecute = false };
        start.Environment["CLAUDE_CONFIG_DIR"] = config;
        start.Environment["DOTNET_BUNDLE_EXTRACT_BASE_DIR"] = MakeDirectory(fixture, "extract");
        start.Environment["Path"] = bin;
        ConfigureFixtureSettings(start, settings);

        using (var app = StartAppearanceFixture(start, ready))
        {
            try
            {
                WaitFor("The dashboard did not load the appearance fixture sessions.", () =>
                    FixtureTitles(DashboardWindow(app)!, id => id.StartsWith("ResumeSession_", StringComparison.Ordinal)).Length == 3);
                CaptureWindow(app.MainWindowHandle, output, "appearance-light.png", settings);

                OpenAppearance(app);
                var range = AppearanceRange(app, "FontScaleSlider");
                Require(range.Current.Minimum == 85 && range.Current.Maximum == 125 && range.Current.Value == 100,
                    "Expected a default 100% font slider with the 85–125% range.");
                range = AppearanceRange(app, "OpacitySlider");
                Require(range.Current.Minimum == 60 && range.Current.Maximum == 100 && range.Current.Value == 100,
                    "Expected a default 100% opacity slider with the 60–100% range.");
                AppearanceRange(app, "FontScaleSlider").SetValue(85);
                WaitFor("Font 85% did not save in the isolated file.", () => ReadAppearance(saved) is { Font: 85 });
                CloseAppearance(app);
                var small = MeasureTitle(DashboardWindow(app)!);

                OpenAppearance(app);
                AppearanceRange(app, "FontScaleSlider").SetValue(125);
                var toggle = (TogglePattern)RequireControl(DashboardWindow(app)!, "DarkModeToggle").GetCurrentPattern(TogglePattern.Pattern);
                Require(toggle.Current.ToggleState == ToggleState.Off, "The first-run theme was not light.");
                toggle.Toggle();
                WaitFor("Dark mode and font 125% did not save in the isolated file.", () => ReadAppearance(saved) is { Dark: true, Font: 125 });
                CloseAppearance(app);
                WaitFor("The actual window title did not grow with the font setting.", () => MeasureTitle(DashboardWindow(app)!) >= small * 1.23);
                CaptureWindow(app.MainWindowHandle, output, "appearance-dark.png", settings);
                var light = MeanLuminance(Path.Combine(output, "appearance-light.png"));
                var dark = MeanLuminance(Path.Combine(output, "appearance-dark.png"));
                Require(light >= dark + 65, "The rendered dashboard did not change to a distinctly dark appearance.");
                Console.WriteLine(FormattableString.Invariant($"OBSERVED appearance-light/dark luminance={light:F1}/{dark:F1}"));

                VerifyRepair(app, "InstallRepairButton", settings, output, "appearance-dark-repair.png");
                InvokeControl(DashboardWindow(app)!, "DockLeftButton");
                WaitFor("The dark app did not dock left at 450 DIP.", () => IsAppearanceDocked(app, left: true));
                OpenAppearance(app);
                Require(IsVisibleInsideWindow(DashboardWindow(app)!, "OpacitySlider"), "The opacity slider was clipped in the left sidebar at 125% font.");
                CaptureWindow(app.MainWindowHandle, output, "appearance-dark-sidebar-settings.png", settings);
                AppearanceRange(app, "OpacitySlider").SetValue(90);
                WaitFor("Changing opacity from the left sidebar did not apply to the native window.", () =>
                    ReadNativeAlpha(app.MainWindowHandle) == 230 && ReadAppearance(saved) is { Opacity: 90 });
                AppearanceRange(app, "OpacitySlider").SetValue(100);
                WaitFor("The sidebar did not return to full opacity.", () => ReadNativeAlpha(app.MainWindowHandle) == 255);
                CloseAppearance(app);
                VerifyRepair(app, "SidebarInstallRepairButton", settings, output, "appearance-dark-sidebar-repair.png");
                InvokeControl(DashboardWindow(app)!, "DockRightButton");
                WaitFor("The dark app did not dock right at 450 DIP.", () => IsAppearanceDocked(app, left: false));
                OpenAppearance(app);
                Require(IsVisibleInsideWindow(DashboardWindow(app)!, "FontScaleSlider"), "The font slider was clipped in the right sidebar at 125% font.");
                AppearanceRange(app, "FontScaleSlider").SetValue(120);
                WaitFor("Changing font size from the right sidebar did not save.", () => ReadAppearance(saved) is { Font: 120 });
                AppearanceRange(app, "FontScaleSlider").SetValue(125);
                WaitFor("The sidebar did not restore font 125%.", () => ReadAppearance(saved) is { Font: 125 });
                CloseAppearance(app);
                VerifyRepair(app, "SidebarInstallRepairButton", settings, null, null);
                Console.WriteLine("OBSERVED dark repair opened on desktop and both 450-DIP sidebars at 125% font.");
                InvokeControl(DashboardWindow(app)!, "UndockButton");
                WaitFor("Appearance checks could not restore the full launcher.", () => FindControl(DashboardWindow(app)!, "LanguagePicker") is not null);

                OpenAppearance(app);
                AppearanceRange(app, "OpacitySlider").SetValue(60);
                WaitFor("Native opacity did not reach 60% and persist.", () =>
                    ReadNativeAlpha(app.MainWindowHandle) == 153 && ReadAppearance(saved) is { Dark: true, Font: 125, Opacity: 60 });
                CloseAppearance(app);
            }
            finally { CloseFixtureProcess(app); }
        }

        using (var reopened = StartAppearanceFixture(start, ready))
        {
            try
            {
                WaitFor("The persisted 60% native opacity did not restore after restart.", () => ReadNativeAlpha(reopened.MainWindowHandle) == 153);
                OpenAppearance(reopened);
                var toggle = (TogglePattern)RequireControl(DashboardWindow(reopened)!, "DarkModeToggle").GetCurrentPattern(TogglePattern.Pattern);
                Require(toggle.Current.ToggleState == ToggleState.On && AppearanceRange(reopened, "FontScaleSlider").Current.Value == 125 &&
                    AppearanceRange(reopened, "OpacitySlider").Current.Value == 60, "Persisted appearance controls did not restore after restart.");
                AppearanceRange(reopened, "OpacitySlider").SetValue(100);
                WaitFor("Native opacity did not restore to 100%.", () => ReadNativeAlpha(reopened.MainWindowHandle) == 255 &&
                    ReadAppearance(saved) is { Opacity: 100 });
                CloseAppearance(reopened);
            }
            finally { CloseFixtureProcess(reopened); }
        }

        Require(userAppearance == FileStamp(Path.Combine(userSettings, "appearance.json")) &&
            userLanguage == FileStamp(Path.Combine(userSettings, "language.txt")), "The user's launcher settings were altered by a fixture.");
        return "Theme, font and native opacity applied immediately, isolated settings restored, and both 450-DIP sidebars and repair were usable in dark 125%.";
    }

    private static Process StartAppearanceFixture(ProcessStartInfo start, string ready)
    {
        File.Delete(ready);
        var app = Process.Start(start) ?? throw new InvalidOperationException("Appearance EXE did not start.");
        try
        {
            WaitFor("The settings isolation hook did not confirm redirection before the GUI started.", () =>
            {
                if (app.HasExited && !File.Exists(ready))
                    throw new BlockedException("The appearance EXE exited before the settings isolation hook verified both paths.");
                return File.Exists(ready) && DashboardWindow(app) is not null;
            });
            return app;
        }
        catch
        {
            CloseFixtureProcess(app);
            app.Dispose();
            throw;
        }
    }

    private static void ConfigureFixtureSettings(ProcessStartInfo start, string settings)
    {
        start.Environment["DOTNET_STARTUP_HOOKS"] = Path.Combine(AppContext.BaseDirectory, "SettingsIsolationHook.dll");
        start.Environment["BLSLAB_QA_SETTINGS_ROOT"] = settings;
    }

    private static void OpenAppearance(Process app)
    {
        WaitFor("The appearance button did not become visible and enabled.", () =>
            FindControl(DashboardWindow(app)!, "AppearanceButton") is { } button && !button.Current.IsOffscreen && button.Current.IsEnabled);
        InvokeControl(DashboardWindow(app)!, "AppearanceButton");
        WaitFor("The appearance popup did not expose the contracted controls.", () =>
        {
            var window = DashboardWindow(app)!;
            return FindControl(window, "DarkModeToggle") is not null && FindControl(window, "FontScaleSlider") is not null &&
                FindControl(window, "OpacitySlider") is not null;
        });
    }

    private static void CloseAppearance(Process app)
    {
        var window = DashboardWindow(app)!;
        var scope = FindControl(window, "AppearancePanel") ?? window;
        var labels = new[] { "ko", "en", "zh-Hans", "ja" }
            .Select(code => LoadResourceDictionary($"Strings.{code}.xaml")["CloseAppearance"] as string)
            .ToHashSet(StringComparer.Ordinal);
        var button = scope.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button))
            .Cast<AutomationElement>().FirstOrDefault(control => !control.Current.IsOffscreen && labels.Contains(control.Current.Name))
            ?? throw new InvalidOperationException("The visible appearance popup close button did not exist.");
        ((InvokePattern)button.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
        WaitFor("The appearance popup did not close.", () => FindControl(DashboardWindow(app)!, "DarkModeToggle") is null);
    }

    private static void VerifyRepair(Process app, string id, string settings, string? output, string? filename)
    {
        var window = DashboardWindow(app)!;
        Require(IsVisibleInsideWindow(window, id), "The install/repair button was not accessible at 125% font.");
        InvokeControl(window, id);
        WaitFor("The repair popup did not show a readable diagnosis and Cancel button.", () =>
        {
            var current = DashboardWindow(app)!;
            return FindControl(current, "ResultTitle") is { } title && !string.IsNullOrWhiteSpace(title.Current.Name) &&
                IsVisibleInsideWindow(current, "ResultTitle") && FindControl(current, "CancelButton") is { } cancel && cancel.Current.IsEnabled;
        }, 100);
        if (filename is not null)
            CaptureWindow(app.MainWindowHandle, output, filename, settings);
        InvokeControl(DashboardWindow(app)!, "CancelButton");
        WaitFor("The repair popup did not close on cancel.", () => FindControl(DashboardWindow(app)!, "CancelButton") is null);
    }

    private static RangeValuePattern AppearanceRange(Process app, string id) =>
        (RangeValuePattern)RequireControl(DashboardWindow(app)!, id).GetCurrentPattern(RangeValuePattern.Pattern);

    private static AutomationElement RequireControl(AutomationElement window, string id) => FindControl(window, id)
        ?? throw new InvalidOperationException($"Appearance automation control '{id}' did not exist.");

    private static double MeasureTitle(AutomationElement window) =>
        RequireControl(window, "WindowTitle").Current.BoundingRectangle.Height;

    private static bool IsVisibleInsideWindow(AutomationElement window, string id) =>
        FindControl(window, id) is { } control && !control.Current.IsOffscreen &&
        window.Current.BoundingRectangle.Contains(control.Current.BoundingRectangle);

    private static bool IsAppearanceDocked(Process app, bool left)
    {
        var (leftGap, rightGap, width) = MeasureDock(app.MainWindowHandle);
        return width is >= 420 and <= 480 && Math.Abs(left ? leftGap : rightGap) < 20 &&
            FindControl(DashboardWindow(app)!, "UndockButton") is not null;
    }

    private static (bool Dark, int Font, int Opacity)? ReadAppearance(string saved)
    {
        try
        {
            using var read = File.Open(saved, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var json = JsonDocument.Parse(read);
            var row = json.RootElement;
            return (row.GetProperty("DarkMode").GetBoolean(), row.GetProperty("FontPercent").GetInt32(), row.GetProperty("OpacityPercent").GetInt32());
        }
        catch (Exception error) when (error is IOException or JsonException) { return null; }
    }

    private static (bool Exists, long Length, DateTime LastWrite) FileStamp(string path)
    {
        var file = new FileInfo(path);
        return file.Exists ? (true, file.Length, file.LastWriteTimeUtc) : default;
    }

    private static double MeanLuminance(string path)
    {
        using var image = File.OpenRead(path);
        var frame = new FormatConvertedBitmap(BitmapDecoder.Create(image, BitmapCreateOptions.IgnoreColorProfile,
            BitmapCacheOption.OnLoad).Frames[0], PixelFormats.Bgra32, null, 0);
        var row = new byte[frame.PixelWidth * 4];
        var total = 0d;
        var samples = 0;
        for (var y = 0; y < frame.PixelHeight; y += 16)
        {
            frame.CopyPixels(new System.Windows.Int32Rect(0, y, frame.PixelWidth, 1), row, row.Length, 0);
            for (var offset = 0; offset < row.Length; offset += 64)
            {
                total += 0.2126 * row[offset + 2] + 0.7152 * row[offset + 1] + 0.0722 * row[offset];
                samples++;
            }
        }
        return total / samples;
    }

    private static int ReadNativeAlpha(IntPtr window)
    {
        if ((GetWindowLong(window, -20) & 0x00080000) == 0)
            return 255;
        return GetLayeredWindowAttributes(window, out _, out var alpha, out var flags) && (flags & 2) != 0 ? alpha : -1;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr window, int index);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLayeredWindowAttributes(IntPtr window, out uint colorKey, out byte alpha, out uint flags);
}
