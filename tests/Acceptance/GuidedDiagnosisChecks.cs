using ClaudeLauncher;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal static partial class Program
{
    private static string TestGuidedDiagnosis(Application app, string? captures)
    {
        var product = typeof(Diagnostics).Assembly;
        var guided = product.GetType("ClaudeLauncher.GuidedDiagnosis", throwOnError: true)!;
        var itemType = product.GetType("ClaudeLauncher.CheckItem", throwOnError: true)!;
        var stateType = product.GetType("ClaudeLauncher.CheckState", throwOnError: true)!;
        var noticeType = product.GetType("ClaudeLauncher.DiagnosticNotice", throwOnError: true)!;

        object Finding(int stage, string key, string state, string? action = null, string? target = null, string? revision = null,
            string evidence = "")
        {
            var item = Activator.CreateInstance(itemType, stage, key, Enum.Parse(stateType, state), evidence, action, target)!;
            itemType.GetProperty("Revision")!.SetValue(item, revision);
            return item;
        }

        object Examine(string method, string code, string? detail, object[] rows)
        {
            var notice = Activator.CreateInstance(noticeType, code, detail, true, null)!;
            var checks = Array.CreateInstance(itemType, rows.Length);
            Array.Copy(rows, checks, rows.Length);
            return Call(guided, method, notice, checks)!;
        }
        object Evaluate(string code, string? detail, params object[] rows) => Examine("Evaluate", code, detail, rows);

        static object? Property(object result, string key) => result.GetType().GetProperty(key)!.GetValue(result);
        static void Expect(object result, string status, object? repair = null, string? quickRepair = null)
        {
            Require(Equals(Property(result, "StatusKey"), status), $"Expected {status}, got {Property(result, "StatusKey")}.");
            Require(ReferenceEquals(Property(result, "Repair"), repair), $"{status} recommended the wrong full repair target.");
            var quick = Property(result, "QuickRepair");
            Require(Equals(quick is null ? null : Property(quick, "Code"), quickRepair),
                $"{status} recommended an unexpected quick action.");
        }

        var pass = Enumerable.Range(1, 8).Select(stage => Finding(stage, stage == 2 ? "InstallBinaryVerified" : "PassingFixture", "Pass")).ToArray();
        Expect(Evaluate("Ready", null, pass), "GuidedHealthy");
        Expect(Evaluate("Ready", null), "GuidedIssue");

        var missing = Finding(2, "InstallBinaryMissing", "Failed");
        object Backup(string version)
        {
            var file = $@"C:\Users\Example\.local\share\claude\versions\{version}";
            var target = file + Environment.NewLine + @"C:\Users\Example\.local\bin\claude.exe";
            var item = Activator.CreateInstance(itemType, 2, "InstallBackupAvailable", Enum.Parse(stateType, "Warning"),
                file, "RestoreClaudeBackup", target)!;
            itemType.GetProperty("Revision")!.SetValue(item, version);
            return item;
        }
        var latest = Backup("2.1.331");
        var older = Backup("2.1.330");
        var reinstall = Finding(2, "InstallNativeRecovery", "Warning", "InstallClaudeNative", "Claude native installer", "native-revision");
        object[] WithBinary(params object[] binary) => [pass[0], .. binary, .. pass.Skip(2)];
        var lostExe = Evaluate("Unknown", "InstallationFootprintFound", WithBinary(missing, latest, older, reinstall));
        Expect(lostExe, "GuidedIssue", latest);
        Require(ReferenceEquals(Property(lostExe, "Finding"), missing), "The suggested repair did not identify the missing executable as its cause.");
        Expect(Evaluate("Unknown", "InstallationFootprintFound", WithBinary(missing, reinstall)), "GuidedIssue", reinstall);
        Expect(Evaluate("Missing", null, WithBinary(missing, reinstall)), "GuidedInstallNeeded", quickRepair: "Missing");
        var noWinGet = Finding(2, "InstallWinGetUnavailable", "Warning");
        Expect(Evaluate("Missing", null, WithBinary(missing, noWinGet, reinstall)), "GuidedInstallNeeded", quickRepair: "Missing");
        var pathMissing = Finding(3, "InstallPathMissing", "Warning", "PreferClaudePath", @"C:\native-bin", "user-PATH-before");
        Expect(Evaluate("PathMissing", @"C:\native-bin", [.. pass, pathMissing]), "GuidedIssue", pathMissing);

        var changedBackup = Finding(2, "InstallBackupAvailable", "Warning", "RestoreClaudeBackup", @"C:\versions\changed");
        Expect(Evaluate("Unknown", "InstallationFootprintFound", WithBinary(missing, changedBackup)), "GuidedIssue");
        var networkFailure = Finding(7, "NetDownloadHtml", "Failed");
        Expect(Evaluate("Ready", null, [.. pass, networkFailure]), "GuidedIssue");

        const string nativeExe = @"C:\Users\Example\.local\bin\claude.exe";
        const string otherExe = @"C:\Tools\claude.exe";
        var unsigned = Finding(2, "InstallBinaryUnsigned", "Failed", evidence: nativeExe + "\nNotSigned");
        var unknown = Finding(2, "InstallBinaryTrustUnknown", "Unknown", evidence: nativeExe + "\nUnknownError");
        foreach (var code in new[] { "BinaryHashMismatch", "BinaryUnsigned", "BinaryOtherPublisher", "BinaryExecutionFailed" })
        {
            var finding = Finding(2, "Install" + code, "Failed", evidence: nativeExe);
            Expect(Evaluate(code, nativeExe, WithBinary(finding, latest, reinstall)), "GuidedIssue", latest);
            Expect(Evaluate(code, nativeExe, WithBinary(finding, reinstall)), "GuidedIssue");
        }
        var wrongTarget = Finding(2, "InstallBackupAvailable", "Warning", "RestoreClaudeBackup",
            @"C:\Tools\signed-backup" + Environment.NewLine + otherExe, "fixture-revision");
        void ExpectReadOnly(string code, string detail, object finding, object backup)
        {
            var rows = WithBinary(finding, backup, reinstall, pathMissing);
            Expect(Evaluate(code, detail, rows), "GuidedIssue");
            var details = (System.Collections.IEnumerable)Examine("ReconcileExecutable", code, detail, rows);
            Require(details.Cast<object>().All(row => Property(row, "Action") is not
                ("RestoreClaudeBackup" or "InstallClaudeNative" or "PreferClaudePath" or "RefreshClaudeProcessPath")),
                $"Expanded details still offered to replace or redirect the executable for {code}.");
        }
        ExpectReadOnly("BinaryTrustUnknown", nativeExe, unknown, latest);
        ExpectReadOnly("BinaryUnsigned", otherExe, unsigned, latest);
        ExpectReadOnly("BinaryHashMismatch", nativeExe, unsigned, latest);
        ExpectReadOnly("BinaryUnsigned", nativeExe, unsigned, wrongTarget);
        ExpectReadOnly("BinaryUnsigned", otherExe, missing, latest);
        ExpectReadOnly("Ready", nativeExe, unsigned, latest);

        AddFixtureStyles(app);
        var korean = LoadDiagnosticResources("ko");
        app.Resources.MergedDictionaries.Add(korean);
        try
        {
            var notice = Activator.CreateInstance(noticeType, "Unknown", "InstallationFootprintFound", true, null)!;
            var panel = (SetupPanel)Activator.CreateInstance(typeof(SetupPanel), BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null, args: [Path.GetTempPath(), null, notice], culture: null)!;
            var rows = WithBinary(missing, latest, older, reinstall);
            var typed = Array.CreateInstance(itemType, rows.Length);
            Array.Copy(rows, typed, rows.Length);
            const BindingFlags member = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            typeof(SetupPanel).GetField("_checks", member)!.SetValue(panel, typed);
            typeof(SetupPanel).GetMethod("Render", member)!.Invoke(panel, null);
            var status = (TextBlock)panel.FindName("GuidedStatus");
            var title = (TextBlock)panel.FindName("ResultTitle");
            var question = ((TextBlock)panel.FindName("ResultBody")).Text;
            var yes = (Button)panel.FindName("RecommendedAction");
            var no = (Button)panel.FindName("CancelButton");
            var details = (Expander)panel.FindName("DetailsExpander");
            Require(status.Text == (string)korean["GuidedIssue"] && title.Text == (string)korean["CheckInstallBinaryMissingTitle"] &&
                question.Contains("2.1.331") && !question.Contains("2.1.330") &&
                yes.Visibility == Visibility.Visible && no.Visibility == Visibility.Visible &&
                Equals(no.Content, korean["GuidedNo"]) && !details.IsExpanded,
                "The Korean missing-executable dialog did not present a single yes/no decision with details collapsed.");
            if (captures is not null)
            {
                var size = new Size(600, 360);
                panel.Measure(size);
                panel.Arrange(new Rect(size));
                panel.UpdateLayout();
                var image = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
                image.Render(panel);
                Directory.CreateDirectory(captures);
                using var stream = File.Create(Path.Combine(captures, "guided-missing-executable-ko.png"));
                var png = new PngBitmapEncoder();
                png.Frames.Add(BitmapFrame.Create(image));
                png.Save(stream);
            }
        }
        finally { app.Resources.MergedDictionaries.Remove(korean); }
        return "Fresh native install without WinGet, missing executable with two backups, native reinstall, user PATH, healthy, incomplete and manual cases returned the expected single recommendation.";
    }
}
