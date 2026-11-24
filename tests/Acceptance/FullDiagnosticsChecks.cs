using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Automation;
using ClaudeLauncher;
using Microsoft.Win32;
using ResourceDictionary = System.Windows.ResourceDictionary;

internal static partial class Program
{
    private static readonly string[] DiagnosticVariables =
    [
        "ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN", "ANTHROPIC_BASE_URL", "NODE_EXTRA_CA_CERTS", "CLAUDE_CODE_GIT_BASH_PATH",
        "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "NO_PROXY", "DISABLE_AUTOUPDATER", "PATH",
    ];
    private static readonly byte[] DiagnosticSnapshotKey = RandomNumberGenerator.GetBytes(32);

    private static string TestFullDiagnosticsScan(string root)
    {
        var fixture = MakeDirectory(root, "full-diagnostics");
        var cwd = MakeDirectory(fixture, "진단 folder");
        var config = MakeDirectory(fixture, "config");
        File.WriteAllText(Path.Combine(config, "settings.json"), "{}");
        var variables = EnvironmentSnapshot();
        var files = Fingerprint(fixture);
        var secret = $"qa-secret-{Guid.NewGuid():N}";
        using var proxy = new OfflineDiagnosticProxy(secret);
        var overrides = new Dictionary<string, string?>
        {
            ["CLAUDE_CONFIG_DIR"] = config,
            ["ANTHROPIC_API_KEY"] = secret,
            ["ANTHROPIC_AUTH_TOKEN"] = secret,
            ["NODE_EXTRA_CA_CERTS"] = Path.Combine(fixture, "missing-ca.pem"),
            ["CLAUDE_CODE_GIT_BASH_PATH"] = Path.Combine(fixture, "wrong-shell.exe"),
            ["HTTP_PROXY"] = proxy.Address,
            ["HTTPS_PROXY"] = proxy.Address,
            ["ALL_PROXY"] = proxy.Address,
            ["NO_PROXY"] = "localhost",
        };
        var before = overrides.Keys.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        DiagnosticRow[] rows;
        try
        {
            foreach (var (name, value) in overrides)
                Environment.SetEnvironmentVariable(name, value);
            var fixtureEnvironment = EnvironmentSnapshot();
            rows = ScanFullDiagnostics(cwd);
            Require(fixtureEnvironment.SequenceEqual(EnvironmentSnapshot()), "The scan changed an environment setting before fixture cleanup.");
        }
        finally
        {
            foreach (var (name, value) in before)
                Environment.SetEnvironmentVariable(name, value);
        }

        Require(variables.SequenceEqual(EnvironmentSnapshot()), "A read-only diagnostic scan changed a process or persisted setting.");
        Require(files.SequenceEqual(Fingerprint(fixture)), "A read-only diagnostic scan changed fixture files.");
        Require(rows.All(row => !row.PublicText.Contains(secret, StringComparison.Ordinal)),
            "A synthetic secret was exposed in diagnostic evidence or its action target.");
        Require(rows.Select(row => row.Stage).Distinct().SequenceEqual(Enumerable.Range(1, 8)),
            "The scan did not return all eight stages in order.");
        Require(rows.All(row => row.Key != "InspectionError"), "A diagnostic group returned only its inspection-error fallback.");
        Require(rows.All(row => row.Action is null || !string.IsNullOrWhiteSpace(row.Target)),
            "A repair action did not specify its affected target.");
        Require(rows.Any(row => row is { Stage: 1, Key: "Windows64", State: "Pass" }) &&
            rows.Any(row => row is { Stage: 1, Key: "ShellInstalled", State: "Pass" } && row.Evidence.Contains("Windows PowerShell", StringComparison.Ordinal)),
            "The Windows x64 and Windows PowerShell version checks did not pass on the test host.");
        foreach (var (name, key) in new[]
        {
            ("ANTHROPIC_API_KEY", "EnvApiKeyManual"), ("ANTHROPIC_AUTH_TOKEN", "EnvAuthTokenManual"),
            ("NODE_EXTRA_CA_CERTS", "EnvCaMissingManual"), ("CLAUDE_CODE_GIT_BASH_PATH", "EnvGitBashInvalidManual"),
        })
        {
            Require(rows.Any(row => row is { Stage: 6, State: "Warning", Action: null } && row.Key == key &&
                row.Target == $"Process: {name}"), $"Missing a process-only warning for {name}.");
        }
        Require(rows.Any(row => row is { Stage: 6, Key: "ProxyConfigured", Action: null, Target: "Process: HTTPS_PROXY" }),
            "The fixture process proxy was not identified.");
        var network = rows.Where(row => row.Stage == 7).ToArray();
        foreach (var host in new[] { "downloads.claude.ai", "api.anthropic.com", "claude.ai" })
            Require(network.Any(row => Regex.IsMatch(row.PublicText, $@"(?<![\w.-]){Regex.Escape(host)}(?![\w.-])") &&
                row.State is "Warning" or "Failed" or "Unknown" && row.Action is null),
                $"The offline endpoint did not report a read-only connectivity finding for {host}.");
        VerifyDiagnosticTranslations(rows);
        return $"Stages 1–8 returned {rows.Length} rows with valid display strings, 3 offline hosts and process-only credential/CA/Git Bash findings. No data changes or secret exposure.";
    }

    private static string TestFullDiagnosticsInvalidApproval(string root)
    {
        var cwd = MakeDirectory(root, "invalid-approval");
        var variables = EnvironmentSnapshot();
        var files = Fingerprint(cwd);
        var assembly = typeof(Diagnostics).Assembly;
        var checkItem = assembly.GetType("ClaudeLauncher.CheckItem", throwOnError: true)!;
        var checkState = assembly.GetType("ClaudeLauncher.CheckState", throwOnError: true)!;
        var initialState = Enum.Parse(checkState, "Warning");
        var diagnostics = assembly.GetType("ClaudeLauncher.FullDiagnostics", throwOnError: true)!;
        bool Apply(object? instance) => ((Task<bool>)Call(diagnostics, "ApplyAsync", cwd, instance, CancellationToken.None)!)
            .GetAwaiter().GetResult();
        bool TryApply(int stage, string key, string? action, string? target)
        {
            var instance = Activator.CreateInstance(checkItem, stage, key, initialState, "", action, target);
            return Apply(instance);
        }

        const string forged = "QA_Unrecognized_Action";
        Require(!TryApply(8, forged, null, cwd) && !TryApply(8, forged, forged, null) &&
            !TryApply(1, forged, forged, cwd) && !TryApply(8, forged, forged, cwd) && !TryApply(6, forged, forged, cwd),
            "A missing, unsupported or forged approval was accepted.");

        const string apiKey = "ANTHROPIC_API_KEY";
        var synthetic = $"qa-stale-{Guid.NewGuid():N}";
        Require(Enum.GetValues<EnvironmentVariableTarget>().All(scope =>
            !string.Equals(Environment.GetEnvironmentVariable(apiKey, scope), synthetic, StringComparison.Ordinal)),
            "The synthetic state unexpectedly matched a live environment value.");
        var environment = assembly.GetType("ClaudeLauncher.EnvironmentChecks", throwOnError: true)!;
        var savedValue = environment.GetNestedType("SavedValue", BindingFlags.NonPublic)!;
        var variableState = environment.GetNestedType("VariableState", BindingFlags.NonPublic)!;
        var fakeUser = Activator.CreateInstance(savedValue, synthetic, "String", synthetic);
        var fakeMachine = Activator.CreateInstance(savedValue, [null, null, null]);
        var fakeState = Activator.CreateInstance(variableState, fakeUser, fakeMachine, synthetic);
        var stale = Call(environment, "VariableItem", cwd, apiKey, "User", fakeState, false, null)!;
        Require(checkItem.GetProperty("Action")!.GetValue(stale) as string == "ClearUserVariable" &&
            checkItem.GetProperty("Revision")!.GetValue(stale) is string,
            "The synthetic user-only value failed to produce a real action and revision.");
        Require(!Apply(stale), "The stale revision for an invented user-only value reached a write.");
        Require(variables.SequenceEqual(EnvironmentSnapshot()) && files.SequenceEqual(Fingerprint(cwd)),
            "An invalid approval changed a fixture file or an environment setting.");

        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        try
        {
            _ = ScanFullDiagnostics(cwd, canceled.Token);
            throw new InvalidOperationException("The scanner accepted a canceled request.");
        }
        catch (OperationCanceledException) { }
        return "Missing, unsupported, forged and stale approvals were rejected. A canceled scan stopped without changes.";
    }

    private static string TestDiagnosticNetworkResponses()
    {
        const string syntheticBody = "QA response body, never include in local evidence";
        var environment = typeof(Diagnostics).Assembly.GetType("ClaudeLauncher.EnvironmentChecks", throwOnError: true)!;
        var download = new Uri("https://downloads.claude.ai/claude-code-releases/latest");
        var api = new Uri("https://api.anthropic.com");
        var cases = new (Uri Uri, HttpStatusCode Status, string Body, HttpRequestError? Error, string Key, string State)[]
        {
            (download, HttpStatusCode.OK, "2.1.4", null, "NetDownloadReady", "Pass"),
            (download, HttpStatusCode.OK, $"<!doctype html>{syntheticBody}", null, "NetDownloadHtml", "Failed"),
            (download, HttpStatusCode.OK, "unavailable in region", null, "NetRegion", "Failed"),
            (download, HttpStatusCode.Forbidden, syntheticBody, null, "NetDownloadForbidden", "Failed"),
            (download, HttpStatusCode.OK, syntheticBody, null, "NetDownloadUnexpected", "Warning"),
            (api, HttpStatusCode.Unauthorized, syntheticBody, null, "NetReachable", "Pass"),
            (api, HttpStatusCode.ProxyAuthenticationRequired, syntheticBody, null, "NetHttpWarning", "Warning"),
            (api, default, "", HttpRequestError.SecureConnectionError, "NetTls", "Failed"),
            (api, default, "", HttpRequestError.NameResolutionError, "NetDns", "Failed"),
            (api, default, "", HttpRequestError.ProxyTunnelError, "NetProxy", "Failed"),
            (api, default, "", HttpRequestError.ConnectionError, "NetTransport", "Failed"),
        };
        var rows = new List<DiagnosticRow>();
        foreach (var scenario in cases)
        {
            using var handler = new DiagnosticHttpHandler(request =>
            {
                Require(request.Method == HttpMethod.Get && request.RequestUri == scenario.Uri &&
                    request.Headers.Authorization is null && request.Headers.ProxyAuthorization is null,
                    "A network probe used a different endpoint or attached authentication.");
                if (scenario.Error is { } code)
                    throw new HttpRequestException(code, syntheticBody);
                return new HttpResponseMessage(scenario.Status) { Content = new StringContent(scenario.Body) };
            });
            using var client = new HttpClient(handler);
            var row = ToDiagnosticRow(CompletedResult((Task)Call(environment, "CheckHostAsync", client,
                scenario.Uri, scenario.Uri == download, CancellationToken.None)!));
            Require(row.Stage == 7 && row.Key == scenario.Key && row.State == scenario.State &&
                row.Action is null && row.Target == scenario.Uri.Host && !row.PublicText.Contains(syntheticBody, StringComparison.Ordinal),
                $"Unexpected network classification for {scenario.Key}.");
            rows.Add(row);
        }
        VerifyDiagnosticTranslations(rows.ToArray());
        return $"{cases.Length} fixed HTTP/TLS/DNS/proxy responses classified with no outbound traffic, credentials or body text in evidence.";
    }

    private static string TestDiagnosticNpmPolicy(string root)
    {
        var npm = MakeDirectory(root, "diagnostics-npm");
        var package = MakeDirectory(npm, Path.Combine("node_modules", "@anthropic-ai", "claude-code"));
        var shim = Path.Combine(npm, "claude.ps1");
        File.WriteAllText(shim, "# Fixture only, never execute. node_modules/@anthropic-ai/claude-code/cli.js\n");
        File.WriteAllText(Path.Combine(package, "package.json"), "{\"name\":\"@anthropic-ai/claude-code\"}");
        var files = Fingerprint(npm);
        var variables = EnvironmentSnapshot();
        var previous = Environment.GetEnvironmentVariable("NPM_CONFIG_PREFIX");
        var previousPath = Environment.GetEnvironmentVariable("PATH");
        var environment = typeof(Diagnostics).Assembly.GetType("ClaudeLauncher.EnvironmentChecks", throwOnError: true)!;
        var shellState = environment.GetNestedType("ShellState", BindingFlags.NonPublic)!;
        var shimState = environment.GetNestedType("NpmShim", BindingFlags.NonPublic)!;
        var rows = new List<DiagnosticRow>();
        try
        {
            Environment.SetEnvironmentVariable("NPM_CONFIG_PREFIX", npm);
            Environment.SetEnvironmentVariable("PATH", npm);
            var observed = CompletedResult((Task)Call(environment, "ReadShellAsync", npm, CancellationToken.None, null)!);
            Require(observed is not null, "The real PowerShell policy probe returned no state.");
            var observedPath = shellState.GetProperty("ClaudePath")!.GetValue(observed) as string;
            var observedType = shellState.GetProperty("ClaudeType")!.GetValue(observed) as string;
            var observedShim = shellState.GetProperty("Shim")!.GetValue(observed);
            var observedSignature = observedShim is null ? null : shimState.GetProperty("SignatureStatus")!.GetValue(observedShim) as string;
            Require(string.Equals(observedPath, shim, StringComparison.OrdinalIgnoreCase) && observedSignature == "NotSigned",
                $"The real npm probe found command type {observedType ?? "(none)"}, fixture path match=" +
                $"{string.Equals(observedPath, shim, StringComparison.OrdinalIgnoreCase)}, signature={observedSignature ?? "(none)"}.");
            var cases = new[]
            {
                ("Restricted", "Undefined", "Undefined", "ExternalScript", shim, 0, "NotSigned", "PolicyNpmBlocked", "UserRemoteSigned"),
                ("AllSigned", "AllSigned", "Undefined", "ExternalScript", shim, 0, "NotSigned", "PolicyGroup", null),
                ("Restricted", "Undefined", "Restricted", "ExternalScript", shim, 0, "NotSigned", "PolicyNpmManual", null),
                ("RemoteSigned", "Undefined", "Undefined", "ExternalScript", shim, 0, "NotSigned", "PolicyNpmAllowed", null),
                ("RemoteSigned", "Undefined", "Undefined", "ExternalScript", shim, 3, "NotSigned", "PolicyNpmManual", null),
                ("AllSigned", "Undefined", "Undefined", "ExternalScript", shim, 0, "UnknownError", "PolicyUnknown", null),
                ("Restricted", "Undefined", "Undefined", "Application", Path.ChangeExtension(shim, ".exe"), 0, "NotSigned", "PolicyNative", null),
            };
            foreach (var (effective, group, process, commandType, commandPath, zone, signature, key, action) in cases)
            {
                var result = Activator.CreateInstance(shellState, effective, group, "Undefined", process,
                    "Undefined", "Undefined", commandType, commandPath, null, false);
                if (commandType == "ExternalScript")
                {
                    var state = Activator.CreateInstance(shimState, "fixture-sha256", zone, "fixture-zone-sha256", signature);
                    shellState.GetProperty("Shim")!.SetValue(result, state);
                }
                var row = ToDiagnosticRow(Call(environment, "PolicyItem", npm, result)!);
                Require(row is { Stage: 5 } && row.Key == key && row.Action == action,
                    $"Unexpected PowerShell policy classification for {key}.");
                if (action is not null)
                    Require(row.Target?.Contains("CurrentUser", StringComparison.Ordinal) == true &&
                        row.Target.Contains("RemoteSigned", StringComparison.Ordinal), "The policy edit was not scoped to CurrentUser RemoteSigned.");
                rows.Add(row);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("NPM_CONFIG_PREFIX", previous);
            Environment.SetEnvironmentVariable("PATH", previousPath);
        }
        Require(files.SequenceEqual(Fingerprint(npm)) && variables.SequenceEqual(EnvironmentSnapshot()),
            "Policy classification changed a fixture file or environment scope.");
        VerifyDiagnosticTranslations(rows.ToArray());
        return "The real probe identified the inert unsigned npm shim. Seven policy/signature cases passed with no user policy change.";
    }

    private static string TestDiagnosticInstallFiles(string root)
    {
        var profile = MakeDirectory(root, "diagnostics-install-profile");
        var bin = MakeDirectory(profile, Path.Combine(".local", "bin"));
        var downloads = MakeDirectory(profile, Path.Combine(".claude", "downloads"));
        var newest = Path.Combine(bin, "claude.exe.old.newest");
        var oldest = Path.Combine(bin, "claude.exe.old.oldest");
        var staging = Path.Combine(bin, "claude.exe.new.synthetic");
        var outside = Path.Combine(profile, "outside.txt");
        var approvedFile = Path.Combine(downloads, "pending.bin");
        foreach (var file in new[] { newest, oldest, staging, outside, approvedFile })
            File.WriteAllText(file, "synthetic fixture");
        File.SetLastWriteTimeUtc(oldest, Older);
        File.SetLastWriteTimeUtc(newest, Newer);
        var install = typeof(Diagnostics).Assembly.GetType("ClaudeLauncher.InstallChecks", throwOnError: true)!;
        Type Nested(string name) => install.GetNestedType(name, BindingFlags.NonPublic)!;
        var paths = IsolatedInstallPaths(install, profile);
        Require((string)paths.GetType().GetProperty("Profile")!.GetValue(paths)! == profile &&
            (string)paths.GetType().GetProperty("Downloads")!.GetValue(paths)! == downloads &&
            (string)paths.GetType().GetProperty("Bin")!.GetValue(paths)! == bin,
            "Install file planning was not isolated to the test profile.");

        var emptyProcesses = Activator.CreateInstance(typeof(List<>).MakeGenericType(Nested("ProcessIdentity")));
        var processScan = Activator.CreateInstance(Nested("ProcessScan"), emptyProcesses, false);
        var planType = Nested("Plan");
        (DiagnosticRow Row, Func<CancellationToken, Task<bool>>? Apply)[] Plans(bool binaryValid)
        {
            var binary = Activator.CreateInstance(Nested("BinaryState"), null, binaryValid, null, null);
            var plans = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(planType))!;
            Call(install, "AddCleanupChecks", plans, paths, binary, null, processScan);
            return plans.Cast<object>().Select(plan => (ToDiagnosticRow(planType.GetProperty("Item")!.GetValue(plan)!),
                (Func<CancellationToken, Task<bool>>?)planType.GetProperty("Apply")!.GetValue(plan))).ToArray();
        }

        var unverified = Plans(binaryValid: false).Single(plan => plan.Row.Key == "InstallDebris");
        Require(unverified.Row.Action is null && unverified.Apply is null,
            "An invalid native EXE still offered to delete potential recovery files.");
        var verified = Plans(binaryValid: true);
        var debris = verified.Single(plan => plan.Row.Key == "InstallDebris");
        var debrisTargets = debris.Row.Target!.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        Require(debris.Row.Action == "CleanClaudeDebris" && debris.Apply is not null &&
            debrisTargets.SetEquals([oldest, staging]) && debris.Row.Evidence.Contains(newest, StringComparison.Ordinal),
            "Debris review did not retain the newest backup or name the exact deletion targets.");

        var variables = EnvironmentSnapshot();
        var download = verified.Single(plan => plan.Row.Key == "InstallDownloads");
        Require(download.Row.Action == "CleanClaudeDownloads" && download.Row.Target == approvedFile && download.Apply is not null,
            "Cache cleanup approval did not list the exact fixture file.");
        File.AppendAllText(approvedFile, "\nchanged after review");
        var beforeCleanup = Fingerprint(profile);
        Require(!download.Apply!(CancellationToken.None).GetAwaiter().GetResult() && beforeCleanup.SequenceEqual(Fingerprint(profile)),
            "A download changed after approval was deleted or altered.");

        var refreshed = Plans(binaryValid: true).Single(plan => plan.Row.Key == "InstallDownloads");
        Require(refreshed.Row.Target == approvedFile && refreshed.Apply is not null, "Rescan lost the changed cache file.");
        Require(refreshed.Apply!(CancellationToken.None).GetAwaiter().GetResult() && !File.Exists(approvedFile) &&
            File.Exists(newest) && File.Exists(oldest) && File.Exists(staging) && File.Exists(outside) &&
            variables.SequenceEqual(EnvironmentSnapshot()), "The accepted fixture cleanup did not remove exactly the approved file.");
        VerifyDiagnosticTranslations(verified.Select(plan => plan.Row).Append(unverified.Row).ToArray());
        return "The newest backup stayed out of deletion targets, an invalid EXE blocked debris cleanup, and a cache file changed after review was preserved until fresh approval.";
    }

    private static string TestDiagnosticCommandOrder(string root)
    {
        var profile = MakeDirectory(root, "diagnostics-command-profile");
        var alias = MakeDirectory(profile, Path.Combine("Microsoft", "WindowsApps"));
        var alternate = MakeDirectory(profile, "another claude");
        var aliasExe = Path.Combine(alias, "claude.exe");
        var alternateExe = Path.Combine(alternate, "claude.exe");
        var fixture = CopyUnsignedCli(profile);
        foreach (var file in Directory.EnumerateFiles(fixture))
        {
            File.Copy(file, Path.Combine(alias, Path.GetFileName(file)));
            File.Copy(file, Path.Combine(alternate, Path.GetFileName(file)));
        }
        var marker = Path.Combine(root, "full-diagnostic-command-marker");
        var beforePath = Environment.GetEnvironmentVariable("PATH");
        var beforeMarker = Environment.GetEnvironmentVariable(MarkerName);
        var variables = EnvironmentSnapshot();
        var files = Fingerprint(profile);
        var install = typeof(Diagnostics).Assembly.GetType("ClaudeLauncher.InstallChecks", throwOnError: true)!;
        var planType = install.GetNestedType("Plan", BindingFlags.NonPublic)!;
        var plans = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(planType))!;
        var paths = IsolatedInstallPaths(install, profile);
        try
        {
            Environment.SetEnvironmentVariable("PATH", $"{alias};{alternate}");
            Environment.SetEnvironmentVariable(MarkerName, marker);
            ((Task)Call(install, "AddPathChecksAsync", plans, paths, null, profile, CancellationToken.None)!).GetAwaiter().GetResult();
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", beforePath);
            Environment.SetEnvironmentVariable(MarkerName, beforeMarker);
        }
        var rows = plans.Cast<object>().Select(plan => ToDiagnosticRow(planType.GetProperty("Item")!.GetValue(plan)!)).ToArray();
        var hit = rows.SingleOrDefault(row => row is { Stage: 4, Key: "InstallDesktopAlias" });
        Require(hit is { State: "Warning", Action: null } && hit.Evidence.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .SequenceEqual([$"1. {aliasExe}", $"2. {alternateExe}"]), "The discovered command order or manual WindowsApps alias finding was incorrect.");
        Require(!File.Exists(marker) && files.SequenceEqual(Fingerprint(profile)) && variables.SequenceEqual(EnvironmentSnapshot()),
            "Looking up conflicting executables ran an unsigned candidate or changed files/settings.");
        VerifyDiagnosticTranslations(rows);
        return "Two inert executables appeared in process lookup order, with WindowsApps first and a manual alias warning. Neither candidate ran.";
    }

    private static string TestDiagnosticProcessTargets(string root)
    {
        var profile = MakeDirectory(root, "diagnostics-process-profile");
        var exe = Path.Combine(profile, ".local", "bin", "claude.exe");
        var install = typeof(Diagnostics).Assembly.GetType("ClaudeLauncher.InstallChecks", throwOnError: true)!;
        Type Nested(string name) => install.GetNestedType(name, BindingFlags.NonPublic)!;
        var paths = IsolatedInstallPaths(install, profile);
        var identity = Nested("ProcessIdentity");
        var processes = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(identity))!;
        processes.Add(Activator.CreateInstance(identity, 10000, 7, Older.Ticks, exe, true));
        processes.Add(Activator.CreateInstance(identity, 10001, 8, Newer.Ticks, exe, false));
        var scan = Activator.CreateInstance(Nested("ProcessScan"), processes, true);
        var planType = Nested("Plan");
        var plans = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(planType))!;
        Call(install, "AddProcessChecks", plans, scan, paths, new[] { exe });
        var rows = plans.Cast<object>().Select(plan => ToDiagnosticRow(planType.GetProperty("Item")!.GetValue(plan)!)).ToArray();
        Require(rows.Length == 3 && rows.Any(row => row is { Stage: 2, Key: "InstallProcessUnknown", State: "Unknown", Action: null }) &&
            rows.Any(row => row is { Stage: 2, Key: "InstallProcessActive", Action: "StopClaudeProcess" } &&
                row.Target == $"PID 10000{Environment.NewLine}{exe}") &&
            rows.Any(row => row is { Stage: 2, Key: "InstallProcessOtherSession", Action: null } &&
                row.Target == $"PID 10001{Environment.NewLine}{exe}"),
            "The process review failed to scope a stop action to the selected PID and executable.");
        plans.Clear();
        Call(install, "AddProcessChecks", plans, scan, paths, Array.Empty<string>());
        var running = plans.Cast<object>().Select(plan => ToDiagnosticRow(planType.GetProperty("Item")!.GetValue(plan)!)).ToArray();
        Require(running.Length == 2 && running.All(row => row is { Key: "InstallProcessRunning", Action: null }),
            "A running Claude process unnecessarily offered to stop when no maintenance was pending.");
        VerifyDiagnosticTranslations(rows.Concat(running).ToArray());
        return "The same-session fixture stop target included exact PID and path. Inaccessible and other-session fixtures offered no stop action.";
    }

    private static object IsolatedInstallPaths(Type install, string profile)
    {
        var type = install.GetNestedType("InstallPaths", BindingFlags.NonPublic)!;
        var paths = Activator.CreateInstance(type, nonPublic: true)!;
        foreach (var name in new[] { "Profile", "LocalAppData", "Roaming" })
        {
            type.GetField($"<{name}>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(paths, profile);
            Require(string.Equals(type.GetProperty(name)!.GetValue(paths) as string, profile, StringComparison.Ordinal),
                $"The fixture install paths did not isolate {name}.");
        }
        return paths;
    }

    private static string TestFullDiagnosticsWindow(string root, string release, string? captures)
    {
        VerifyFixtureOnlyProfile(recencyIsolated: true);
        var fixture = MakeDirectory(root, "full-diagnostics-ui");
        var cwd = MakeDirectory(fixture, "선택 folder");
        var config = MakeDirectory(fixture, "config");
        var settings = MakeDirectory(fixture, "launcher-settings");
        var emptyPath = MakeDirectory(fixture, "empty-path");
        File.WriteAllText(Path.Combine(settings, "language.txt"), "en");
        File.WriteAllText(Path.Combine(config, "settings.json"), "{}");
        var initialized = Path.Combine(settings, "isolated.ready");
        var launcherSettings = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ClaudeCodeSetupDiagnostics");
        var userSettings = new[] { "appearance.json", "language.txt" }.Select(name => Path.Combine(launcherSettings, name)).ToArray();
        var beforeSettings = userSettings.Select(FileStamp).ToArray();
        var beforeEnv = EnvironmentSnapshot();
        var secret = $"qa-ui-secret-{Guid.NewGuid():N}";
        using var proxy = new OfflineDiagnosticProxy(secret);
        var start = new ProcessStartInfo(release) { UseShellExecute = false, WorkingDirectory = cwd };
        start.Environment["CLAUDE_CONFIG_DIR"] = config;
        start.Environment["ANTHROPIC_API_KEY"] = secret;
        start.Environment.Remove("ANTHROPIC_AUTH_TOKEN");
        start.Environment.Remove("ANTHROPIC_BASE_URL");
        start.Environment["HTTPS_PROXY"] = proxy.Address;
        start.Environment["HTTP_PROXY"] = proxy.Address;
        start.Environment["ALL_PROXY"] = proxy.Address;
        start.Environment["NO_PROXY"] = "localhost";
        start.Environment["PATH"] = emptyPath;
        ConfigureFixtureSettings(start, settings);

        using var app = StartAppearanceFixture(start, initialized);
        var variables = EnvironmentSnapshot();
        var files = Fingerprint(fixture);
        var labels = LoadDiagnosticResources("en");
        AutomationElement Window() => DashboardWindow(app)!;
        bool Enabled(string id) => FindControl(Window(), id) is { } control && control.Current.IsEnabled;
        bool Showing(string id) => FindControl(Window(), id) is { } control && !control.Current.IsOffscreen;
        bool childPathRefresh = false;
        void AssertNoUnapprovedWrites()
        {
            Require(beforeEnv.SequenceEqual(variables) && variables.SequenceEqual(EnvironmentSnapshot()),
                "The diagnostic changed the parent environment or persisted settings.");
            Require(beforeSettings.SequenceEqual(userSettings.Select(FileStamp)),
                "The diagnostic changed the user's launcher appearance or language settings.");
            var current = Fingerprint(fixture);
            if (!files.SequenceEqual(current))
            {
                var changed = files.Except(current).Concat(current.Except(files)).Select(entry => entry.Split('|', 2)[0])
                    .Distinct().ToArray();
                var isolatedConfig = changed.All(path => path is @"config\.claude.json" ||
                    Regex.IsMatch(path, @"^config\\backups\\\.claude\.json\.backup\.\d+$"));
                Require(childPathRefresh && isolatedConfig, $"The diagnostic changed fixture files: {string.Join(", ", changed)}.");
            }
        }
        void CaptureDiagnostic(string filename)
        {
            if (captures is null)
                return;
            CaptureWindow(app.MainWindowHandle, captures, filename, settings);
            WaitFor("The capture fixture did not finish its request.", () => !File.Exists(Path.Combine(settings, "capture.request")));
            File.Delete(Path.Combine(settings, "frame.png"));
        }
        void AssertApprovalCanBeCanceled(string targetFragment, string? capture = null)
        {
            WaitFor("The separate final confirmation did not open.", () =>
                Enabled("ConfirmApprovalButton") && Enabled("CancelApprovalButton") && Showing("ApprovalTarget"));
            var target = FindControl(Window(), "ApprovalTarget")!.Current.Name;
            Require(!string.IsNullOrWhiteSpace(target) && target.Contains(targetFragment, StringComparison.OrdinalIgnoreCase),
                "The final confirmation did not name the approved change target.");
            Require(!target.Contains(secret, StringComparison.Ordinal), "A synthetic secret appeared in the final confirmation.");
            AssertNoUnapprovedWrites();
            if (capture is not null)
                CaptureDiagnostic(capture);
            InvokeControl(Window(), "CancelApprovalButton");
            WaitFor("Cancel did not return from the final confirmation.", () => !Showing("ConfirmApprovalButton"));
            AssertNoUnapprovedWrites();
        }
        AutomationElement? Named(AutomationElement scope, string name) => scope.FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.NameProperty, name));
        AutomationElement Stage(string number)
        {
            var title = $"{number}. {labels["CheckStage" + number]}";
            var stages = FindControl(Window(), "FullDiagnosticStages")!;
            return stages.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ClassNameProperty, "Expander"))
                .Cast<AutomationElement>().FirstOrDefault(element => element.Current.Name == title ||
                    element.Current.Name.StartsWith(title + ",", StringComparison.Ordinal))
                ?? throw new InvalidOperationException($"Stage {number} did not appear in the full diagnostic.");
        }
        void Expand(AutomationElement stage)
        {
            if (stage.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var expand))
            {
                var pattern = (ExpandCollapsePattern)expand;
                if (pattern.Current.ExpandCollapseState == ExpandCollapseState.Collapsed)
                    pattern.Expand();
                return;
            }
            var header = FindControl(stage, "HeaderSite");
            Require(header is not null && header.TryGetCurrentPattern(TogglePattern.Pattern, out _),
                "The stage did not provide an accessible expansion control.");
            var toggle = (TogglePattern)header!.GetCurrentPattern(TogglePattern.Pattern);
            if (toggle.Current.ToggleState == ToggleState.Off)
                toggle.Toggle();
        }

        try
        {
            SetInput(Window(), "FolderInput", cwd);
            InvokeControl(Window(), "InstallRepairButton");
            void WaitForSummary() => WaitFor("Opening setup did not automatically present the diagnostic summary.", () =>
                Showing("GuidedStatus") && Showing("ResultTitle") && Enabled("CancelButton") && Enabled("DetailsExpander"), 100);
            WaitForSummary();
            var details = FindControl(Window(), "DetailsExpander")!;
            Require(!Showing("ReviewCheckAction") && !Showing("ConfirmApprovalButton") &&
                ((ExpandCollapsePattern)details.GetCurrentPattern(ExpandCollapsePattern.Pattern)).Current.ExpandCollapseState ==
                    ExpandCollapseState.Collapsed, "Expert controls were expanded on entry.");
            var status = FindControl(Window(), "GuidedStatus")!.Current.Name;
            Require(status == (string)labels["GuidedIssue"] || status == (string)labels["GuidedInstallNeeded"],
                "The fixture with no network and an empty PATH was reported as healthy.");
            CaptureDiagnostic("guided-diagnosis.png");
            AssertNoUnapprovedWrites();
            InvokeControl(Window(), "CancelButton");
            WaitFor("Declining the guided result did not close setup.", () => FindControl(Window(), "SetupModal") is null);
            AssertNoUnapprovedWrites();

            InvokeControl(Window(), "InstallRepairButton");
            WaitForSummary();
            Expand(FindControl(Window(), "DetailsExpander")!);
            WaitFor("Expanding details did not expose the existing scan.", () => FindControl(Window(), "FullDiagnosticStages") is not null);
            foreach (var number in Enumerable.Range(1, 8))
                _ = Stage(number.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Require(!Showing("ConfirmApprovalButton"), "The scan enabled a final repair before an action was reviewed.");
            CaptureDiagnostic("full-diagnostics.png");

            var environment = Stage("6");
            Expand(environment);
            foreach (var key in new[] { "CheckEnvApiKeyManualTitle", "CheckEnvApiKeyManualCause", "CheckEnvApiKeyManualSolution" })
                WaitFor($"The detailed view did not render {key}.", () => Named(environment, (string)labels[key]!) is not null);
            var visibleNames = environment.FindAll(TreeScope.Descendants, Condition.TrueCondition).Cast<AutomationElement>()
                .Select(control => control.Current.Name);
            Require(visibleNames.All(name => !name.Contains(secret, StringComparison.Ordinal)),
                "A synthetic secret was exposed in the visible diagnostics.");
            Console.WriteLine("OBSERVED eight detailed stages and the process-only API-key cause and solution verified with no secret exposure");

            var path = Stage("3");
            Expand(path);
            var review = FindControl(path, "ReviewCheckAction");
            if (review is { } && review.Current.IsEnabled && review.TryGetCurrentPattern(InvokePattern.Pattern, out var action))
            {
                var refreshLabel = labels["FixRefreshClaudeProcessPath"] as string;
                var refreshTitle = labels["CheckInstallPathStaleTitle"] as string;
                var processOnly = refreshLabel is not null && refreshTitle is not null &&
                    review.Current.Name == $"{refreshLabel}, {refreshTitle}";
                ((InvokePattern)action).Invoke();
                AssertApprovalCanBeCanceled(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "full-diagnostics-approval.png");
                Console.WriteLine("OBSERVED detailed PATH target and explicit cancellation verified");

                if (processOnly)
                {
                    var currentReview = FindControl(Stage("3"), "ReviewCheckAction")!;
                    ((InvokePattern)currentReview.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
                    WaitFor("The isolated process-PATH repair did not reopen its final confirmation.", () =>
                        Enabled("ConfirmApprovalButton") && Showing("ApprovalTarget"));
                    var target = FindControl(Window(), "ApprovalTarget")!.Current.Name;
                    var nativeBin = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin");
                    Require(target.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
                        .SequenceEqual(["Claude Launcher process PATH", nativeBin]),
                        "The expected fixture-only process PATH action listed a different target. No repair was confirmed.");
                    InvokeControl(Window(), "ConfirmApprovalButton");
                    WaitFor("The child-only PATH refresh did not report a verified update.", () =>
                        Enabled("CancelButton") && Named(Window(), (string)labels["FullApplied"]!) is not null, 100);
                    Expand(FindControl(Window(), "DetailsExpander")!);
                    Expand(Stage("3"));
                    Require(FindControl(Stage("3"), "ReviewCheckAction") is null,
                        "The stale PATH action still appeared after the verified refresh.");
                    childPathRefresh = true;
                    AssertNoUnapprovedWrites();
                }
            }

            InvokeControl(Window(), "CancelButton");
            WaitFor("Closing the diagnostic did not restore the original selected folder.", () =>
                FindControl(Window(), "FullDiagnosticStages") is null && ReadInput(Window(), "FolderInput") == cwd);
            AssertNoUnapprovedWrites();
            return "The guided summary opened automatically; declining made no changes. Eight expandable stages and credential masking passed. " +
                (childPathRefresh ? "The approved PATH refresh was limited to the child process. CLI config/bookkeeping, if created, stayed in the fixture." :
                    "No PATH change was applied.");
        }
        finally
        {
            if (!app.HasExited && (!app.CloseMainWindow() || !app.WaitForExit(5000)))
                throw new BlockedException("The isolated diagnostic GUI did not close normally.");
        }
    }

    private static DiagnosticRow[] ScanFullDiagnostics(string cwd, CancellationToken token = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(75));
        var diagnostics = typeof(Diagnostics).Assembly.GetType("ClaudeLauncher.FullDiagnostics", throwOnError: true)!;
        var items = (IEnumerable)CompletedResult((Task)Call(diagnostics, "ScanAsync", cwd, deadline.Token)!);
        return items.Cast<object>().Select(ToDiagnosticRow).ToArray();
    }

    private static object CompletedResult(Task task)
    {
        task.GetAwaiter().GetResult();
        return task.GetType().GetProperty("Result")!.GetValue(task)!;
    }

    private static DiagnosticRow ToDiagnosticRow(object item)
    {
        object? Read(string name) => item.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)!.GetValue(item);
        return new((int)Read("Stage")!, (string)Read("Key")!, Read("State")!.ToString()!,
            (string)Read("Evidence")!, (string?)Read("Action"), (string?)Read("Target"));
    }

    private static void VerifyDiagnosticTranslations(DiagnosticRow[] rows)
    {
        var keys = rows.SelectMany(row => new[] { $"Check{row.Key}Title", $"Check{row.Key}Cause", $"Check{row.Key}Solution" })
            .Concat(rows.Where(row => row.Action is not null).Select(row => $"Fix{row.Action}"))
            .Distinct(StringComparer.Ordinal).ToArray();
        var resources = LoadDiagnosticResources("en");
        foreach (var key in keys)
            Require(resources.Contains(key) && resources[key] is string label && !string.IsNullOrWhiteSpace(label),
                $"A diagnostic finding refers to a missing display string: {key}.");
    }

    private static ResourceDictionary LoadDiagnosticResources(string language)
    {
        var resources = new ResourceDictionary();
        foreach (var family in new[] { "Strings", "CoreChecks", "InstallChecks", "EnvChecks" })
            resources.MergedDictionaries.Add(LoadResourceDictionary($"{family}.{language}.xaml"));
        return resources;
    }

    private static string?[] EnvironmentSnapshot()
    {
        static string? FingerprintSetting(string? value)
        {
            if (value is null)
                return null;
            var bytes = Encoding.UTF8.GetBytes(value);
            try { return Convert.ToHexString(HMACSHA256.HashData(DiagnosticSnapshotKey, bytes)); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        static string? Policy(RegistryKey hive)
        {
            try
            {
                using var key = hive.OpenSubKey(@"SOFTWARE\Microsoft\PowerShell\1\ShellIds\Microsoft.PowerShell");
                return key?.GetValue("ExecutionPolicy") as string;
            }
            catch (Exception error) when (error is SecurityException or UnauthorizedAccessException)
            {
                return $"Unavailable ({error.GetType().Name})";
            }
        }
        return [.. Enum.GetValues<EnvironmentVariableTarget>()
            .SelectMany(scope => DiagnosticVariables.Select(name => FingerprintSetting(Environment.GetEnvironmentVariable(name, scope)))),
            FingerprintSetting(Policy(Registry.CurrentUser)), FingerprintSetting(Policy(Registry.LocalMachine))];
    }

    private sealed record DiagnosticRow(int Stage, string Key, string State, string Evidence, string? Action, string? Target)
    {
        public string PublicText => string.Join('\n', Key, State, Evidence, Action, Target);
    }

    private sealed class OfflineDiagnosticProxy : IDisposable
    {
        private readonly Socket _socket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

        public OfflineDiagnosticProxy(string secret)
        {
            _socket.ExclusiveAddressUse = true;
            _socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            // Reserve a local port without listening, so no vendor endpoint or proxy receives a request.
            Address = $"http://qa:{secret}@127.0.0.1:{((IPEndPoint)_socket.LocalEndPoint!).Port}";
        }

        public string Address { get; }

        public void Dispose() => _socket.Dispose();
    }

    private sealed class DiagnosticHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(respond(request));
        }
    }
}
