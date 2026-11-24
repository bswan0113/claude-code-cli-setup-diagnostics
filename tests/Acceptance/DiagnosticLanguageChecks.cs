using System.Diagnostics;
using System.IO;

internal static partial class Program
{
    private static string TestDiagnosticStartupLanguages(string root, string release)
    {
        VerifyFixtureOnlyProfile(recencyIsolated: true);
        var fixture = MakeDirectory(root, "diagnostic-startup-languages");
        var emptyPath = MakeDirectory(fixture, "empty-path");
        var variables = EnvironmentSnapshot();
        using var proxy = new OfflineDiagnosticProxy($"qa-ui-language-{Guid.NewGuid():N}");
        foreach (var language in new[] { "en", "ko", "ja", "zh-Hans" })
        {
            var languageRoot = MakeDirectory(fixture, language);
            var settings = MakeDirectory(languageRoot, "settings");
            var config = MakeDirectory(languageRoot, "config");
            var cwd = MakeDirectory(languageRoot, "diagnostic folder");
            File.WriteAllText(Path.Combine(settings, "language.txt"), language);
            var start = new ProcessStartInfo(release) { UseShellExecute = false, WorkingDirectory = cwd };
            start.Environment["CLAUDE_CONFIG_DIR"] = config;
            start.Environment.Remove("ANTHROPIC_API_KEY");
            start.Environment.Remove("ANTHROPIC_AUTH_TOKEN");
            start.Environment.Remove("ANTHROPIC_BASE_URL");
            start.Environment["HTTPS_PROXY"] = proxy.Address;
            start.Environment["HTTP_PROXY"] = proxy.Address;
            start.Environment["ALL_PROXY"] = proxy.Address;
            start.Environment["NO_PROXY"] = "localhost";
            start.Environment["PATH"] = emptyPath;
            ConfigureFixtureSettings(start, settings);
            using var app = StartAppearanceFixture(start, Path.Combine(settings, "isolated.ready"));
            var resources = LoadDiagnosticResources(language);
            try
            {
                var initial = DashboardWindow(app)!;
                SetInput(initial, "FolderInput", cwd);
                InvokeControl(initial, "InstallRepairButton");
                WaitFor($"The clean {language} launch did not show its localized setup dialog and full-scan entry.", () =>
                {
                    var window = DashboardWindow(app)!;
                    return FindControl(window, "SetupModal") is { } modal &&
                        modal.Current.Name == (string)resources["InstallRepairTitle"] &&
                        FindControl(window, "FullScanButton") is { } full && full.Current.IsEnabled &&
                        FindControl(window, "CancelButton") is { } cancel && cancel.Current.Name == (string)resources["CancelSetup"];
                }, 45);
                InvokeControl(DashboardWindow(app)!, "CancelButton");
                WaitFor($"Cancel left the {language} startup dialog open.", () => FindControl(DashboardWindow(app)!, "SetupModal") is null);
            }
            finally
            {
                if (!app.HasExited && (!app.CloseMainWindow() || !app.WaitForExit(5000)))
                    throw new BlockedException($"The isolated {language} diagnostic GUI did not close normally.");
            }
        }
        Require(variables.SequenceEqual(EnvironmentSnapshot()), "A localized startup changed the test parent or persisted environment.");
        return "English, Korean, Japanese and Simplified Chinese each loaded from a fresh isolated startup and showed the localized install/repair dialog.";
    }
}
