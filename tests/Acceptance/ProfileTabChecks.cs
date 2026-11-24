using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using ClaudeLauncher;

internal static partial class Program
{
    private static int RunProfileTabsFixture()
    {
        var temp = Directory.CreateTempSubdirectory("ClaudeLauncherTabs-");
        try
        {
            var product = typeof(MainWindow).Assembly;
            var local = MakeDirectory(temp.FullName, "local");
            var a = MakeDirectory(temp.FullName, "account-a");
            foreach (var key in new[] { "PROFILE_ID", "PROFILE_LABEL", "ORIGINAL_CONFIG", "PROFILE_LOGIN", "PROFILE_HANDOFF" })
                Environment.SetEnvironmentVariable("BLSLAB_CLAUDE_LAUNCHER_" + key, null);
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", a);
            StartupHook.Redirect(product, "ClaudeLauncher.ProfileDataPaths", "LocalData", local);

            var store = MakeDirectory(local, Path.Combine("BlsLab", "ClaudeLauncher", "profiles"));
            var profileId = Guid.NewGuid().ToString("N");
            var b = MakeDirectory(store, Path.Combine(profileId, "claude"));
            string labelB = "Fixture B";
            File.WriteAllText(Path.Combine(store, "profiles.json"), JsonSerializer.Serialize(new
            {
                Version = 1, Profiles = new[] { new { Id = profileId, Label = labelB } }
            }));
            // Fail closed before creating WPF controls or loading any product settings.
            GuardPath("LauncherProfiles", "Store", store);
            GuardPath("LauncherProfiles", "RegistryPath", Path.Combine(store, "profiles.json"));
            GuardPath("UiLanguage", "PreferencePath", local);
            GuardPath("AppearancePreferences", "Store", local);
            GuardPath("SessionRecency", "Store", local);
            GuardPath("FolderHistory", "StorePath", local);
            GuardPath("ProjectRemoval", "Store", local);
            GuardPath("ProfileDataPaths", "Config", a);

            var same = Guid.NewGuid();
            var workspaceA = MakeDirectory(temp.FullName, "project-a");
            var workspaceB = MakeDirectory(temp.FullName, "project-b");
            var ownerA = Fixture(a, workspaceA, "Account A", same);
            var ownerB = Fixture(b, workspaceB, "Account B", same);
            var helpers = CopyUnsignedCli(temp.FullName);
            using var first = StartHeldFixture(temp.FullName, helpers, workspaceA, "stop-a");
            using var second = StartHeldFixture(temp.FullName, helpers, workspaceB, "stop-b");
            WriteActivity(MakeDirectory(a, "sessions"), first.Process.Id, same, "busy", workspaceA, first.StartFileTime, first.StartFileTime);
            WriteActivity(MakeDirectory(b, "sessions"), second.Process.Id, same, "waiting", workspaceB, second.StartFileTime, second.StartFileTime);

            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
            app.Resources.MergedDictionaries.Add(LoadResourceDictionary("MainWindowStyles.xaml"));
            Call(product.GetType("ClaudeLauncher.UiLanguage", true)!, "Apply", "en");
            var window = new MainWindow { ShowInTaskbar = false, ShowActivated = false };
            var hwnd = new WindowInteropHelper(window).EnsureHandle();
            app.MainWindow = window;
            var watch = Stopwatch.StartNew();
            try
            {
                // Drive the actual loaded and tab click handlers on a hidden HWND and the STA dispatcher.
                window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                View("default", workspaceA, ownerA, "Account A", "Default account", first.Process.Id, "Working");
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var accountA = ChildEnvironment(Tab("default")!.Tag!, release.Task);
                Click(profileId);
                View(profileId, workspaceB, ownerB, "Account B", labelB, second.Process.Id, "Waiting");
                var accountB = ChildEnvironment(Tab(profileId)!.Tag!, release.Task);
                release.SetResult();
                Require(Task.WaitAll([accountA, accountB], TimeSpan.FromSeconds(2)), "The bound launches did not finish.");
                Require(accountA.Result.GetValueOrDefault("CLAUDE_CONFIG_DIR") == a &&
                    accountA.Result.GetValueOrDefault("ANTHROPIC_API_KEY") == "synthetic-a" &&
                    accountB.Result.GetValueOrDefault("CLAUDE_CONFIG_DIR") == b &&
                    !accountB.Result.ContainsKey("ANTHROPIC_API_KEY") &&
                    Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") == a,
                    "An asynchronous launch used the other profile's environment or modified the parent process.");
                Click("default");
                View("default", workspaceA, ownerA, "Account A", "Default account", first.Process.Id, "Working");
                Require(!first.Process.HasExited && !second.Process.HasExited, "A tab switch stopped a fixture terminal.");
                Console.WriteLine($"PASS QA-PROFILES-01 | A -> B -> A, same HWND and PID, 2 simultaneous runtime records, " +
                    $"same session UUID in both profiles, per-profile title/activity/owner tooltip, 2 bound child environments; {watch.Elapsed.TotalSeconds:F1}s.");
                return 0;
            }
            finally { window.Close(); app.Shutdown(); }

            void GuardPath(string typeName, string member, string expected)
            {
                var type = product.GetType("ClaudeLauncher." + typeName, true)!;
                var path = (type.GetField(member, AnyStatic)?.GetValue(null) ??
                    type.GetProperty(member, AnyStatic)?.GetValue(null)) as string;
                Require(path is not null && (path.Equals(expected, StringComparison.OrdinalIgnoreCase) ||
                    path.StartsWith(expected + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)),
                    $"Could not isolate {typeName}.{member}.");
            }

            Task<Dictionary<string, string?>> ChildEnvironment(object selected, Task gate)
            {
                var profiles = product.GetType("ClaudeLauncher.LauncherProfiles", true)!;
                using var scope = (IDisposable)Call(profiles, "Bind", selected)!;
                return Task.Run(async () =>
                {
                    await gate.ConfigureAwait(false);
                    var env = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                        { ["ANTHROPIC_API_KEY"] = "synthetic-a" };
                    Call(profiles, "ApplyCliEnvironment", env);
                    return env;
                });
            }

            Button? Tab(string id) => ((ItemsControl)window.FindName("ProfileTabs")).Items.OfType<Button>()
                .FirstOrDefault(button => AutomationProperties.GetAutomationId(button) == "AccountTab_" + id);
            void Click(string id) => (Tab(id) ?? throw new InvalidOperationException("Missing account tab."))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            void View(string id, string cwd, Guid owner, string title, string label, int pid, string activity)
            {
                var expectedDetail = string.Format((string)app.FindResource("SessionOwnerMatched"),
                    owner.ToString("N")[..8] + "…", label);
                string state = "";
                while (watch.Elapsed < TimeSpan.FromSeconds(15))
                {
                    var folders = ((ItemsControl)window.FindName("FolderTree")).Items.Cast<object>().ToArray();
                    var active = ((ItemsControl)window.FindName("RunningTerminals")).Items.Cast<object>().ToArray();
                    state = $"folders={folders.Length}, live={active.Length}, selected={Tab(id) is { } tab && AutomationProperties.GetItemStatus(tab) != ""}";
                    if (folders.Length == 1 && active.Length == 1 && Tab(id) is { } chosen &&
                        AutomationProperties.GetItemStatus(chosen) == (string)app.FindResource("ProfileCurrent"))
                    {
                        var children = ((System.Collections.IEnumerable)Value(folders[0], "Children")!).Cast<object>().ToArray();
                        var saved = children.Length == 1 ? Value(children[0], "Session") as SavedSession : null;
                        string? detail = children.Length == 1 && Value(children[0], "Usage") is { } usage
                            ? Value(usage, "Detail") as string : null;
                        var terminal = Value(active[0], "Terminal")!;
                        state += $", title={saved?.Title}, detail={detail}, activity={Value(active[0], "Activity")}";
                        if (saved is not null && saved.Id == same && saved.Title == title && saved.Cwd == cwd &&
                            Equals(Value(active[0], "Title"), title) && Equals(Value(active[0], "Activity"), activity) &&
                            Equals(Value(terminal, "ProcessId"), pid) && detail?.Contains(expectedDetail, StringComparison.Ordinal) == true)
                        {
                            GetWindowThreadProcessId(hwnd, out var windowPid);
                            Require(app.Windows.Count == 1 && ReferenceEquals(app.MainWindow, window) &&
                                new WindowInteropHelper(window).Handle == hwnd && windowPid == Environment.ProcessId,
                                "The window or process changed while switching account tabs.");
                            return;
                        }
                    }
                    var frame = new DispatcherFrame();
                    var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(25) };
                    timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
                    timer.Start();
                    Dispatcher.PushFrame(frame);
                }
                throw new TimeoutException($"Fixture view {label} did not settle: {state}");
            }
        }
        catch (Exception ex) { Console.Error.WriteLine($"FAIL QA-PROFILES-01 | {ex.GetBaseException().Message}"); return 1; }
        finally
        {
            if (temp.Parent?.FullName.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())),
                StringComparison.OrdinalIgnoreCase) == true && temp.Name.StartsWith("ClaudeLauncherTabs-", StringComparison.Ordinal))
                temp.Delete(recursive: true);
        }

        static object? Value(object source, string field) => source.GetType().GetProperty(field,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(source);
        static Guid Fixture(string config, string cwd, string title, Guid id)
        {
            var account = Guid.NewGuid();
            File.WriteAllText(Path.Combine(config, ".claude.json"), JsonSerializer.Serialize(new { oauthAccount = new { accountUuid = account } }));
            var bucket = MakeDirectory(config, Path.Combine("projects", "bucket"));
            File.WriteAllLines(Path.Combine(bucket, id + ".jsonl"), [
                JsonSerializer.Serialize(new { type = "user", cwd, sessionId = id, uuid = Guid.NewGuid(), message = new { role = "user", content = title } }),
                JsonSerializer.Serialize(new { type = "session-owner", sessionId = id, accountUuid = account, organizationUuid = Guid.NewGuid() })]);
            return account;
        }
    }

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint pid);
}
