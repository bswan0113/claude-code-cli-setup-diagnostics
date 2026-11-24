using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Automation;

internal static partial class Program
{
    private static string TestDashboardWindow(string root, string release, string? captures)
    {
        if (!File.Exists(release) || !Environment.UserInteractive)
            throw new BlockedException("The integrated EXE or an interactive desktop was unavailable.");
        VerifyFixtureOnlyProfile(recencyIsolated: true);

        var local = MakeDirectory(root, "dashboard-fixtures");
        using var displayFolders = new DashboardFolderPaths(captures, local);
        var config = MakeDirectory(local, "claude-config");
        var state = MakeDirectory(config, "sessions");
        var project = MakeDirectory(config, Path.Combine("projects", "dashboard-project"));
        var folder = MakeDirectory(displayFolders.Root, "QA 작업 폴더 [대화] & 로그");
        var secondFolder = MakeDirectory(displayFolders.Root, "QA Second Work Folder");
        var bin = CopyUnsignedCli(displayFolders.Root);
        var titles = Enumerable.Range(1, 8).Select(index => $"QA Fixture Session {index:00}").ToArray();
        var ids = titles.Select(_ => Guid.NewGuid()).ToArray();
        for (var index = 0; index < ids.Length; index++)
        {
            var id = ids[index];
            var cwd = index < 4 ? folder : secondFolder;
            var prompt = UserLine(id, cwd, index is 4 or 5 ? "QA original prompt" : titles[index], Older);
            var fileTime = Latest.AddMinutes(-index);
            if (index == 4)
                WriteSession(project, id, fileTime, prompt, SessionTitleLine(id, "custom-title", "customTitle", titles[index]));
            else if (index == 5)
                WriteSession(project, id, fileTime, prompt, SessionTitleLine(id, "ai-title", "aiTitle", titles[index]));
            else
                WriteSession(project, id, fileTime, prompt);
        }

        using var first = StartHeldFixture(local, bin, folder, "hold-a");
        using var second = StartHeldFixture(local, bin, folder, "hold-b");
        WriteActivity(state, first.Process.Id, ids[0], "busy", folder, first.StartFileTime, first.StartFileTime);
        WriteActivity(state, second.Process.Id, ids[2], "waiting", folder, second.StartFileTime);

        var package = MakeDirectory(local, "package");
        var exe = Path.Combine(package, Path.GetFileName(release));
        File.Copy(release, exe);
        var start = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = package };
        start.Environment["CLAUDE_CONFIG_DIR"] = config;
        start.Environment["DOTNET_BUNDLE_EXTRACT_BASE_DIR"] = MakeDirectory(local, "extract");
        start.Environment["Path"] = bin;
        var settings = MakeDirectory(local, "settings");
        ConfigureFixtureSettings(start, settings);
        var marker = Path.Combine(local, "unexpected-cli-call.txt");
        start.Environment[MarkerName] = marker;

        var userPath = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User);
        var machinePath = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.Machine);
        using var process = StartAppearanceFixture(start, Path.Combine(settings, "isolated.ready"));
        try
        {
            WaitFor("The integrated WPF dashboard did not open.", () => DashboardWindow(process) is not null);
            WaitFor("Expected two live rows and the five newest eligible rows, with no shared IDs.", () =>
            {
                var window = DashboardWindow(process)!;
                var live = FixtureTitles(window, id => id == "FocusTerminalButton");
                var recent = FixtureTitles(window, id => id.StartsWith("ResumeSession_", StringComparison.Ordinal));
                return live.Order(StringComparer.Ordinal).SequenceEqual([titles[0], titles[2]]) &&
                    recent.SequenceEqual([titles[1], titles[3], titles[4], titles[5], titles[6]]);
            });
            CheckTreeSearch(process, titles[4], titles[5]);
            WaitFor("Search text did not clear before the dashboard capture.", () =>
                ReadInput(DashboardWindow(process)!, "HistorySearch") == "");
            if (FindControl(DashboardWindow(process)!, "FolderTree") is { } tree)
                ExpandFixtureFolders(tree);
            Thread.Sleep(160);
            TryCaptureWindow(process.MainWindowHandle, captures, "dashboard.png", settings);

            var desktop = ReadDesktopBounds(process.MainWindowHandle);
            InvokeControl(DashboardWindow(process)!, "DockLeftButton");
            Thread.Sleep(650);
            TryCaptureWindow(process.MainWindowHandle, captures, "sidebar-left.png", settings);
            var left = CheckSide(process, left: true);
            Require(desktop.Equals(ReadDesktopBounds(process.MainWindowHandle)), "Left sidebar changed desktop bounds.");
            InvokeControl(DashboardWindow(process)!, "DockRightButton");
            Thread.Sleep(650);
            TryCaptureWindow(process.MainWindowHandle, captures, "sidebar-right.png", settings);
            var right = CheckSide(process, left: false);
            Require(desktop.Equals(ReadDesktopBounds(process.MainWindowHandle)), "Right sidebar changed desktop bounds.");
            var twoRowsGap = CheckLiveSidebarGap(process, 2);
            InvokeControl(DashboardWindow(process)!, "UndockButton");

            WaitFor("Folder input disappeared after restoring the main layout.", () =>
                FindControl(DashboardWindow(process)!, "FolderInput") is { } field && !field.Current.IsOffscreen);
            SetInput(DashboardWindow(process)!, "FolderInput", folder);
            InvokeControl(DashboardWindow(process)!, "InstallRepairButton");
            WaitFor("Install/repair did not show a read-only diagnostic in its modal.", () =>
            {
                var window = DashboardWindow(process)!;
                var modal = FindControl(window, "SetupModal");
                var notice = FindControl(modal ?? window, "ResultTitle");
                var cancel = FindControl(modal ?? window, "CancelButton");
                return notice is not null && !string.IsNullOrWhiteSpace(notice.Current.Name) &&
                    cancel is not null && cancel.Current.IsEnabled &&
                    FindControl(window, "InstallRepairButton") is { } background && !background.Current.IsEnabled;
            }, 100);
            TryCaptureWindow(process.MainWindowHandle, captures, "repair-modal.png", settings);
            InvokeControl(DashboardWindow(process)!, "CancelButton");
            WaitFor("Cancel did not close the modal and restore the selected folder.", () =>
            {
                var window = DashboardWindow(process)!;
                return FindControl(window, "CancelButton") is null &&
                    ReadInput(window, "FolderInput") == folder;
            });
            Require(!File.Exists(marker) && userPath == Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User) &&
                machinePath == Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.Machine),
                "Read-only dashboard checks ran the unsigned CLI or modified persistent PATH.");
            first.Stop();
            WaitFor("The exited live entry was not removed and promoted to the newest eligible position independently.", () =>
            {
                var window = DashboardWindow(process)!;
                return FixtureTitles(window, id => id == "FocusTerminalButton").SequenceEqual([titles[2]]) &&
                    FixtureTitles(window, id => id.StartsWith("ResumeSession_", StringComparison.Ordinal))
                        .SequenceEqual([titles[0], titles[1], titles[3], titles[4], titles[5]]);
            });
            Require(left && right, "One or both sidebar rectangles did not match the intended edge and 450 DIP width.");

            InvokeControl(DashboardWindow(process)!, "DockRightButton");
            Thread.Sleep(650);
            TryCaptureWindow(process.MainWindowHandle, captures, "sidebar-right-one-session.png", settings);
            var oneRowGap = CheckLiveSidebarGap(process, 1);
            Require(Math.Abs(oneRowGap - twoRowsGap) <= 12, "Removing one live row did not preserve the inter-section spacing.");
            InvokeControl(DashboardWindow(process)!, "UndockButton");
            return "Live/recent split, title search, sidebar docking and one/two-row spacing, and read-only repair cancel all passed.";
        }
        finally
        {
            CloseFixtureProcess(process);
        }
    }

    private static AutomationElement? DashboardWindow(Process process)
    {
        process.Refresh();
        if (process.HasExited)
            throw new InvalidOperationException($"Dashboard EXE exited early with code {process.ExitCode}.");
        return process.MainWindowHandle == IntPtr.Zero ? null : AutomationElement.FromHandle(process.MainWindowHandle);
    }

    private static string[] FixtureTitles(AutomationElement scope, Func<string, bool> matches) => scope.FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button))
        .Cast<AutomationElement>().Where(button => matches(button.Current.AutomationId))
        .Select(button => button.Current.Name.Split(',', 2)[0]).ToArray();

    private static void CheckTreeSearch(Process process, params string[] titles)
    {
        const string field = "HistorySearch";
        foreach (var title in titles)
        {
            SetInput(DashboardWindow(process)!, field, title);
            WaitFor("The folder tree did not show the matching session title from the saved record.", () =>
            {
                var tree = FindControl(DashboardWindow(process)!, "FolderTree");
                return tree is not null && FixtureTitles(tree, id => id.StartsWith("TreeSession_", StringComparison.Ordinal))
                    .SequenceEqual([title]);
            });
        }
        SetInput(DashboardWindow(process)!, field, "");
    }

    private static bool CheckSide(Process process, bool left)
    {
        var unpin = FindControl(DashboardWindow(process)!, "UndockButton");
        var dock = MeasureDock(process.MainWindowHandle);
        Console.WriteLine(FormattableString.Invariant(
            $"OBSERVED sidebar-{(left ? "left" : "right")} width={dock.WidthDip:F1} DIP leftGap={dock.LeftGap}px rightGap={dock.RightGap}px unpin={unpin is not null}"));
        return unpin is not null && Math.Abs(left ? dock.LeftGap : dock.RightGap) < 20 && dock.WidthDip is >= 420 and <= 480;
    }

    private static double CheckLiveSidebarGap(Process process, int expectedRows)
    {
        var window = DashboardWindow(process)!;
        var buttons = window.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button))
            .Cast<AutomationElement>().Select(button => button.Current).Where(button => !button.IsOffscreen).ToArray();
        var live = buttons.Where(button => button.AutomationId == "FocusTerminalButton").ToArray();
        var recent = buttons.Where(button => button.AutomationId.StartsWith("ResumeSession_", StringComparison.Ordinal)).ToArray();
        Require(live.Length == expectedRows && recent.Length > 0, "Sidebar did not show the expected live and recent rows.");
        var gap = (recent.Min(row => row.BoundingRectangle.Top) - live.Max(row => row.BoundingRectangle.Bottom)) *
            96 / GetDpiForWindow(process.MainWindowHandle);
        Console.WriteLine(FormattableString.Invariant($"OBSERVED sidebar-live-{expectedRows}-rows gap-to-recent={gap:F1} DIP"));
        Require(gap is >= 0 and <= 88, "The short live list left excess space before the recent list.");
        return gap;
    }

    private static void InvokeControl(AutomationElement scope, string id)
    {
        var control = FindControl(scope, id) ?? throw new InvalidOperationException($"Automation control '{id}' was not found.");
        Require(control.Current.IsEnabled && control.TryGetCurrentPattern(InvokePattern.Pattern, out _),
            $"Automation control '{id}' could not be invoked.");
        ((InvokePattern)control.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
    }

    private static string? ReadInput(AutomationElement scope, string id) =>
        FindControl(scope, id) is { } input && input.TryGetCurrentPattern(ValuePattern.Pattern, out var value)
            ? ((ValuePattern)value).Current.Value : null;

    private static void SetInput(AutomationElement scope, string id, string value)
    {
        var input = FindControl(scope, id) ?? throw new InvalidOperationException($"Input '{id}' was not found.");
        ((ValuePattern)input.GetCurrentPattern(ValuePattern.Pattern)).SetValue(value);
    }

    private static HeldFixture StartHeldFixture(string root, string bin, string cwd, string name)
    {
        var stop = Path.Combine(root, name);
        var info = new ProcessStartInfo(Path.Combine(bin, "claude.exe"))
        {
            WorkingDirectory = cwd, UseShellExecute = false, CreateNoWindow = true,
        };
        var runtime = new DirectoryInfo(RuntimeEnvironment.GetRuntimeDirectory());
        info.Environment["DOTNET_ROOT"] = runtime.Parent!.Parent!.Parent!.FullName;
        info.Environment.Remove(MarkerName);
        info.Environment.Remove("CLAUDE_QA_CONSOLE_READY");
        info.Environment.Remove("DOTNET_STARTUP_HOOKS");
        info.ArgumentList.Add("--qa-hold");
        info.ArgumentList.Add(stop);
        var process = Process.Start(info) ?? throw new InvalidOperationException("Synthetic process did not start.");
        return new HeldFixture(process, stop);
    }

    private sealed class HeldFixture(Process process, string stop) : IDisposable
    {
        public Process Process => process;
        public long StartFileTime => process.StartTime.ToUniversalTime().ToFileTimeUtc();

        public void Stop()
        {
            if (process.HasExited)
                return;
            File.WriteAllText(stop, "stop");
            if (!process.WaitForExit(5000))
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }

        public void Dispose()
        {
            try
            {
                Stop();
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    private static void CloseFixtureProcess(Process process)
    {
        if (!process.HasExited && (!process.CloseMainWindow() || !process.WaitForExit(5000)))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(5000);
        }
    }

    private static void VerifyFixtureOnlyProfile(bool recencyIsolated = false)
    {
        // Session JSONL is redirected by the child environment. The app's own store is still per user.
        var launcher = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BlsLab", "ClaudeLauncher");
        if (File.Exists(Path.Combine(launcher, "recent-folders.json")) ||
            !recencyIsolated && Directory.Exists(Path.Combine(launcher, "session-launches")))
            throw new BlockedException("Use a test profile without launcher history so the GUI cannot read or capture personal records.");
    }

    private sealed class DashboardFolderPaths : IDisposable
    {
        private readonly string? _parent;

        public DashboardFolderPaths(string? captures, string fallback)
        {
            if (captures is null)
            {
                Root = fallback;
                return;
            }
            _parent = Path.GetFullPath(Path.Combine(captures, ".."));
            Root = MakeDirectory(_parent, $"dashboard-cwd-{Guid.NewGuid():N}");
        }

        public string Root { get; }

        public void Dispose()
        {
            if (_parent is not null && string.Equals(new DirectoryInfo(Root).Parent?.FullName, _parent, StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileName(Root).StartsWith("dashboard-cwd-", StringComparison.Ordinal))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
