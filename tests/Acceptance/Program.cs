using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using ClaudeLauncher;

internal static partial class Program
{
    private const string MarkerName = "CLAUDE_LAUNCHER_QA_MARKER";
    private static readonly DateTime Older = new(2026, 11, 20, 10, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Newer = Older.AddDays(1);
    private static readonly DateTime Latest = Older.AddDays(2);
    private static readonly BindingFlags AnyStatic = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length is not (0 or 2) || args.Length == 2 && args[0] != "--release")
        {
            Console.Error.WriteLine("Usage: Acceptance [--release <path-to-published-exe>]");
            return 2;
        }

        var root = Directory.CreateTempSubdirectory("ClaudeLauncherAcceptance-");
        try
        {
            var failed = 0;
            var passed = 0;
            var blocked = 0;

            void Run(string id, Func<string> test)
            {
                try
                {
                    Console.WriteLine($"PASS {id} | {test()}");
                    passed++;
                }
                catch (BlockedException exception)
                {
                    Console.WriteLine($"BLOCKED {id} | {exception.Message}");
                    blocked++;
                }
                catch (Exception exception)
                {
                    Console.WriteLine($"FAIL {id} | {exception.GetBaseException().Message}");
                    failed++;
                }
            }

            Run("QA-HIS-01", () => TestHistoryMerge(root.FullName));
            Run("QA-HIS-02", () => TestDamagedHistory(root.FullName));
            Run("QA-RUN-01", () => TestDirectInput(root.FullName));
            Run("QA-DIA-01", () => TestReadOnlyDiagnosis(root.FullName));
            Run("QA-DIA-02", () => TestMissingCommand(root.FullName));
            Run("QA-SES-01", () => TestSessionMetadata(root.FullName));
            Run("QA-SES-02", () => TestSessionDamage(root.FullName));
            Run("QA-SES-03", () => TestResumeArguments(root.FullName));
            Run("QA-SES-04", () => TestResumeWithUnsignedCli(root.FullName));
            Run("QA-EXE-01", () => args.Length == 0
                ? throw new BlockedException("No published EXE was specified.")
                : TestPublishedWindow(root.FullName, Path.GetFullPath(args[1])));

            Console.WriteLine($"SUMMARY passed={passed} failed={failed} blocked={blocked}");
            return failed > 0 ? 1 : blocked > 0 && args.Length == 2 ? 3 : 0;
        }
        finally
        {
            var temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
            if (string.Equals(root.Parent?.FullName, temp, StringComparison.OrdinalIgnoreCase) &&
                root.Name.StartsWith("ClaudeLauncherAcceptance-", StringComparison.Ordinal))
            {
                root.Delete(recursive: true);
            }
        }
    }

    private static string TestHistoryMerge(string root)
    {
        var config = MakeDirectory(root, "recent-config");
        var folderA = MakeDirectory(root, Path.Combine("A", "교육 실습"));
        var folderB = MakeDirectory(root, Path.Combine("B", "교육 실습"));
        var local = Path.Combine(config, "launcher-history.json");
        File.WriteAllText(local, JsonSerializer.Serialize(new RecentFolder[]
        {
            new(folderA, Older),
            new(folderB, Newer),
            new(folderA + Path.DirectorySeparatorChar, Latest),
        }));

        var folders = ReadFixtureHistory(local);
        Require(folders.Length == 2, "Expected two distinct paths with the same folder name.");
        Require(folders[0].Path == folderA && folders[0].LastUsedUtc == Latest,
            "Newest launcher timestamp or path normalization was lost.");
        Require(folders[1].Path == folderB && folders[1].LastUsedUtc == Newer,
            "A distinct launcher path with the same name was omitted or reordered.");
        return "Two equal leaf names, two full paths, newest timestamp retained per path.";
    }

    private static string TestDamagedHistory(string root)
    {
        var config = MakeDirectory(root, "damaged-config");
        var localOnly = MakeDirectory(root, "런처 기록");
        var local = Path.Combine(config, "launcher-history.json");
        File.WriteAllText(local, "{truncated-local-store");
        var before = Fingerprint(config);

        Require(ReadFixtureHistory(local).Length == 0, "Malformed history did not degrade to an empty recent-folder list.");
        Require(before.SequenceEqual(Fingerprint(config)), "Recent-folder read changed fixture files.");
        File.WriteAllText(local, JsonSerializer.Serialize(new RecentFolder[] { new(localOnly, Older) }));
        var folders = ReadFixtureHistory(local);
        Require(folders.Length == 1 && folders[0].Path == localOnly,
            "A restored valid launcher record did not load.");
        return "Malformed launcher history degraded to an empty list without writes; a valid record loaded after replacement.";
    }

    private static RecentFolder[] ReadFixtureHistory(string local)
    {
        // The public API selects the per-user store. Bind the production reader to the fixture file.
        var history = typeof(FolderHistory);
        var folders = (Dictionary<string, RecentFolder>)Call(history, "ReadLocal", local)!;
        return ((IEnumerable<RecentFolder>)Call(history, "Sort", folders)!).ToArray();
    }

    private static string TestDirectInput(string root)
    {
        var folder = MakeDirectory(root, "한글 공백 [검증] & 프로젝트");
        var app = new Application();
        try
        {
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("/ClaudeLauncher;component/App/Resources/Strings.en.xaml", UriKind.Relative),
            });
            var window = new MainWindow();
            try
            {
                var input = (TextBox)window.FindName("FolderInput");
                var open = (Button)window.FindName("OpenButton");
                var parse = typeof(MainWindow).GetMethod("TryGetFolder", BindingFlags.NonPublic | BindingFlags.Instance)
                    ?? throw new MissingMethodException(typeof(MainWindow).FullName, "TryGetFolder");

                input.Text = $"  \"{folder}\"  ";
                object?[] values = [null];
                Require(open.IsEnabled && parse.Invoke(window, values) is true && (string?)values[0] == folder,
                    "The direct-input WPF handler changed or rejected the exact absolute folder.");
                input.Text = "relative-folder";
                values[0] = null;
                Require(parse.Invoke(window, values) is false && values[0] is null,
                    "The direct-input WPF handler accepted a relative folder.");
            }
            finally
            {
                window.Close();
            }
        }
        finally
        {
            app.Shutdown();
        }

        return "Real WPF input preserved the quoted special-character path and rejected relative input.";
    }

    private static string TestReadOnlyDiagnosis(string root)
    {
        var folder = MakeDirectory(root, "진단 [읽기 전용] & 폴더");
        var bin = CopyUnsignedCli(root);
        var marker = Path.Combine(root, "cli-called.txt");
        var originalProcess = Environment.GetEnvironmentVariable("Path");
        var originalUser = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User);
        var originalMachine = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.Machine);
        var originalMarker = Environment.GetEnvironmentVariable(MarkerName);
        var before = Fingerprint(root);

        try
        {
            Environment.SetEnvironmentVariable("Path", bin, EnvironmentVariableTarget.Process);
            Environment.SetEnvironmentVariable(MarkerName, marker, EnvironmentVariableTarget.Process);
            CheckUnsignedCliMarker(bin, folder, marker);
            var result = Diagnostics.CheckAsync(folder).GetAwaiter().GetResult();
            var detail = result.Detail?.Split('\n', 2);
            Require((result.Code is "Unknown" or "Conflict") && detail is ["SignatureUnverified", var path] &&
                    string.Equals(path, Path.Combine(bin, "claude.exe"), StringComparison.OrdinalIgnoreCase),
                $"Expected an unverified candidate, observed {result.Code}; candidate detail present={result.Detail is not null}.");
            Require(!File.Exists(marker), "Diagnostic code executed the unsigned candidate.");
            Require(Environment.GetEnvironmentVariable("Path") == bin, "Diagnosis changed the test process PATH.");

            var invalid = Diagnostics.CheckAsync(Path.Combine(folder, "missing")).GetAwaiter().GetResult();
            Require(invalid.Code == "InvalidFolder", $"Expected InvalidFolder, observed {invalid.Code}.");
            Require(!File.Exists(marker), "Directory validation executed the candidate.");
            return $"Unsigned candidate classified {result.Code} and never ran. InvalidFolder and unchanged PATH/files observed.";
        }
        finally
        {
            Environment.SetEnvironmentVariable("Path", originalProcess, EnvironmentVariableTarget.Process);
            Environment.SetEnvironmentVariable(MarkerName, originalMarker, EnvironmentVariableTarget.Process);
            Require(originalUser == Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User) &&
                originalMachine == Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.Machine),
                "User or machine PATH changed during read-only diagnosis.");
            Require(before.SequenceEqual(Fingerprint(root)), "Read-only diagnosis changed the fixture tree.");
        }
    }

    private static void CheckUnsignedCliMarker(string bin, string folder, string marker)
    {
        var start = new ProcessStartInfo(Path.Combine(bin, "claude.exe"))
        {
            WorkingDirectory = folder,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        };
        var runtime = new DirectoryInfo(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory());
        start.Environment["DOTNET_ROOT"] = runtime.Parent!.Parent!.Parent!.FullName;
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Unsigned CLI control did not start.");
        if (!process.WaitForExit(5000))
        {
            process.Kill(entireProcessTree: true);
            throw new InvalidOperationException("Unsigned CLI control timed out.");
        }
        Require(process.ExitCode == 0 && process.StandardOutput.ReadToEnd().Trim() == "99.98.97 (Claude Code)",
            "Unsigned CLI control did not emit the expected Claude-shaped version.");
        Require(File.Exists(marker) && File.ReadAllText(marker).TrimEnd() == folder,
            "Unsigned CLI control did not leave the expected launch marker.");
        File.Delete(marker);
    }

    private static string TestMissingCommand(string root)
    {
        var folder = MakeDirectory(root, "명령 없음 [검증] & 폴더");
        var emptyPath = MakeDirectory(root, "empty-path");
        var probe = typeof(Diagnostics).Assembly.GetType("ClaudeLauncher.PowerShellProbe", throwOnError: true)!;
        var task = (Task)Call(probe, "RunAsync", folder, emptyPath, null, false)!;
        task.GetAwaiter().GetResult();
        var result = task.GetType().GetProperty("Result")!.GetValue(task);
        Require(result is not null, "PowerShell did not return a probe result.");
        var kind = result!.GetType().GetProperty("Kind")!.GetValue(result);
        Require(Equals(kind, "Absent"), $"Expected Absent from an empty child PATH, observed {kind}.");
        return "Production PowerShell probe returned Absent with a fixture-only PATH and special-character working directory.";
    }

    private static string TestPublishedWindow(string root, string release)
    {
        if (!File.Exists(release))
            throw new BlockedException("The specified published EXE is missing.");
        if (!Environment.UserInteractive)
            throw new BlockedException("No interactive desktop is available.");

        var isolated = MakeDirectory(root, "smoke-package");
        var config = MakeDirectory(root, "smoke-claude-config");
        var project = MakeDirectory(config, Path.Combine("projects", "smoke-project"));
        var cwd = MakeDirectory(root, "smoke-session-cwd");
        var fallback = MakeDirectory(root, "smoke-fallback-cwd");
        var id = Guid.NewGuid();
        const string sessionTitle = "QA synthetic session title";
        WriteSession(project, id, Older, UserLine(id, cwd, sessionTitle, Latest));
        WriteSession(project, Guid.NewGuid(), Newer, "{\"invalid-session\":");
        var damagedId = Guid.NewGuid();
        WriteSession(project, damagedId, Latest, "{\"invalid-before-cwd\":",
            UserLine(damagedId, fallback, "qa-damaged-title-must-not-be-a-resume-row", Latest));
        var path = Path.Combine(isolated, Path.GetFileName(release));
        File.Copy(release, path);
        var sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        var bin = CopyUnsignedCli(MakeDirectory(root, "smoke-fixture"));
        var marker = Path.Combine(root, "smoke-cli-called.txt");
        var start = new ProcessStartInfo(path) { UseShellExecute = false, WorkingDirectory = isolated };
        start.Environment["CLAUDE_CONFIG_DIR"] = config;
        start.Environment["DOTNET_BUNDLE_EXTRACT_BASE_DIR"] = MakeDirectory(root, "smoke-extract");
        start.Environment["Path"] = bin;
        start.Environment[MarkerName] = marker;

        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the published EXE.");
        try
        {
            var deadline = Stopwatch.StartNew();
            var clicked = false;
            while (deadline.Elapsed < TimeSpan.FromSeconds(25))
            {
                process.Refresh();
                if (process.HasExited)
                    throw new InvalidOperationException($"Published EXE exited before opening a window, exit={process.ExitCode}.");
                if (process.MainWindowHandle != IntPtr.Zero)
                {
                    var window = AutomationElement.FromHandle(process.MainWindowHandle);
                    var input = FindControl(window, "FolderInput");
                    var picker = FindControl(window, "SessionPickerButton");
                    var buttons = window.FindAll(TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button))
                        .Cast<AutomationElement>().ToArray();
                    var hasHealthyTitle = buttons.Any(button => button.Current.Name.Contains(sessionTitle, StringComparison.Ordinal));
                    var knownFallback = buttons.FirstOrDefault(button => string.Equals(button.Current.Name, fallback, StringComparison.OrdinalIgnoreCase));
                    var hasDamagedTitle = buttons.Any(button => button.Current.Name.Contains("qa-damaged-title", StringComparison.Ordinal));
                    if (!clicked && input is not null && hasHealthyTitle && knownFallback is not null && !hasDamagedTitle &&
                        picker is not null && !picker.Current.IsOffscreen &&
                        knownFallback.TryGetCurrentPattern(InvokePattern.Pattern, out var invoke))
                    {
                        ((InvokePattern)invoke).Invoke();
                        clicked = true;
                    }
                    if (clicked && input is not null && input.TryGetCurrentPattern(ValuePattern.Pattern, out var inputValue) &&
                        string.Equals(((ValuePattern)inputValue).Current.Value, fallback, StringComparison.OrdinalIgnoreCase) &&
                        FindControl(window, "ResultFolder") is { } result &&
                        string.Equals(result.Current.Name, fallback, StringComparison.OrdinalIgnoreCase))
                    {
                        Require(!File.Exists(marker), "The EXE executed the unsigned CLI while validating the fallback folder.");
                        return $"Single copied EXE showed both fallback options and routed the known folder without a dialog; SHA-256={sha256}.";
                    }
                }
                Thread.Sleep(100);
            }
            throw new InvalidOperationException(clicked
                ? "The selected fallback folder was not confirmed in the result within 25 seconds."
                : "WPF home, healthy title and both fallback options were not all observed within 25 seconds.");
        }
        finally
        {
            if (!process.HasExited && (!process.CloseMainWindow() || !process.WaitForExit(5000)))
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
    }

    private static AutomationElement? FindControl(AutomationElement window, string id) =>
        window.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, id));

    private static string CopyUnsignedCli(string root)
    {
        var bin = MakeDirectory(root, "unsigned cli");
        var source = Path.Combine(AppContext.BaseDirectory, "unsigned-cli");
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(bin, Path.GetFileName(file)));
        Require(File.Exists(Path.Combine(bin, "claude.exe")), "The unsigned CLI fixture was not built.");
        return bin;
    }

    private static string[] Fingerprint(string root) => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal)
        .Select(path => $"{Path.GetRelativePath(root, path)}|{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))}")
        .ToArray();

    private static object? Call(Type type, string method, params object?[] args) =>
        (type.GetMethod(method, AnyStatic) ?? throw new MissingMethodException(type.FullName, method)).Invoke(null, args);

    private static string MakeDirectory(string root, string name) => Directory.CreateDirectory(Path.Combine(root, name)).FullName;

    private static void Require(bool condition, string error)
    {
        if (!condition)
            throw new InvalidOperationException(error);
    }

    private sealed class BlockedException(string message) : Exception(message);
}
