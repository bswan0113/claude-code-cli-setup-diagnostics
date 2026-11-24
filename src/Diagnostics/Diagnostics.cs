using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using Microsoft.Win32;

namespace ClaudeLauncher;

public sealed record DiagnosticResult(string Code, string? Detail);

public static class Diagnostics
{
    private const string PackageId = "Anthropic.ClaudeCode";
    internal const string NativeInstallCommand = "irm https://claude.ai/install.ps1 | iex";
    private const string PortablePackageDirectory = PackageId + "_Microsoft.Winget.Source_8wekyb3d8bbwe";
    internal const string OfficialInstallHelp = "https://code.claude.com/docs/en/setup#install-claude-code";
    private const string UserEnvironmentKey = "Environment";
    private const string MachineEnvironmentKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment";
    private static readonly TimeSpan InstallerTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan InstallerExitTimeout = TimeSpan.FromSeconds(5);

    public static Task<DiagnosticResult> CheckAsync(string cwd) =>
        Task.Run(() => RunAsync(cwd, repairCode: null, expectedDetail: null));

    internal static Task<DiagnosticResult> CheckWithCancellationAsync(string cwd, CancellationToken token) =>
        Task.Run(() => RunAsync(cwd, null, null, token), token);

    public static Task<DiagnosticResult> RepairAsync(string cwd, string code) =>
        RepairAsync(cwd, code, expectedDetail: null);

    public static Task<DiagnosticResult> RepairAsync(string cwd, string code, string? expectedDetail) =>
        Task.Run(() => RepairWithLock(cwd, code, expectedDetail));

    private static DiagnosticResult RepairWithLock(string cwd, string code, string? expectedDetail)
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            if (identity.User is not { } user)
                return new("RepairFailed", "RepairLockUnavailable");

            using var mutex = new Mutex(false, $@"Global\BlsLab.ClaudeLauncher.Repair.{user.Value}");
            try
            {
                if (!mutex.WaitOne(0))
                    return new("RepairFailed", "RepairInProgress");
            }
            catch (AbandonedMutexException)
            {
                // Windows grants ownership. Reinspect the installation before deciding what to do.
            }

            try
            {
                // A mutex must be released by the thread that acquired it.
                return RunAsync(cwd, code, expectedDetail).GetAwaiter().GetResult();
            }
            finally
            {
                mutex.ReleaseMutex();
            }
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or
            SecurityException or WaitHandleCannotBeOpenedException)
        {
            return new("RepairFailed", "RepairLockUnavailable");
        }
    }

    private static async Task<DiagnosticResult> RunAsync(string cwd, string? repairCode, string? expectedDetail, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(cwd) || !Path.IsPathFullyQualified(cwd) || !Directory.Exists(cwd))
            return new("InvalidFolder", null);

        try
        {
            var current = await InspectAsync(cwd, token).ConfigureAwait(false);
            // The UI approves a particular action. A changed finding requires a new decision.
            if (current.Code != repairCode)
                return current;

            if (current.Code is "PathMissing" or "StalePath" &&
                (current.Detail is null || expectedDetail is null || !SamePath(current.Detail, expectedDetail)))
            {
                return current;
            }

            return current.Code switch
            {
                "Missing" => await RepairJournal.TrackAsync("InstallClaudeNative", () => InstallAsync(cwd),
                    done => done.Code is "Ready" or "AuthRequired" or "PathMissing" or "StalePath").ConfigureAwait(false),
                "PathMissing" when current.Detail is { } directory =>
                    await RepairJournal.TrackAsync("PreferClaudePath", () => ActivatePathAsync(cwd, directory, persist: true),
                        done => done.Code is "Ready" or "AuthRequired").ConfigureAwait(false),
                "StalePath" when current.Detail is { } directory =>
                    await RepairJournal.TrackAsync("RefreshClaudeProcessPath", () => ActivatePathAsync(cwd, directory, persist: false),
                        done => done.Code is "Ready" or "AuthRequired").ConfigureAwait(false),
                _ => current
            };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            SecurityException or Win32Exception or ArgumentException or InvalidOperationException)
        {
            var issue = exception is UnauthorizedAccessException or SecurityException
                ? "AccessDenied" : "EnvironmentCheckFailed";
            return new(repairCode is null ? "Unknown" : "RepairFailed", issue);
        }
    }

    private static async Task<DiagnosticResult> InspectAsync(string cwd, CancellationToken token = default)
    {
        // Match the launcher first: Windows PowerShell without a profile, using the app process's PATH.
        var live = await PowerShellProbe.RunWithCancellationAsync(cwd, token, checkAuth: true).ConfigureAwait(false);
        if (live.Kind != "Absent")
            return ClassifyResolvedCommand(live);

        var storedPath = ReadStoredPath();
        if (!string.Equals(storedPath, Environment.GetEnvironmentVariable("Path"), StringComparison.Ordinal))
        {
            var fresh = await PowerShellProbe.RunWithCancellationAsync(cwd, token, pathOverride: storedPath).ConfigureAwait(false);
            if (fresh is { VerifiedVersion: true, Path: not null })
                return new("StalePath", Path.GetDirectoryName(fresh.Path));

            if (fresh.Kind != "Absent")
                return ClassifyResolvedCommand(fresh);
        }

        var candidates = new List<string>();
        foreach (var candidatePath in KnownExecutables())
        {
            try
            {
                if ((File.GetAttributes(candidatePath) & FileAttributes.Directory) != 0)
                    return new("BinaryTrustUnknown", candidatePath);
                candidates.Add(candidatePath);
            }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException)
            {
                return new("BinaryTrustUnknown", candidatePath);
            }
        }
        var installed = candidates.DistinctBy(ExecutableTarget, StringComparer.OrdinalIgnoreCase).ToArray();
        if (installed.Length > 1)
            return new("Conflict", installed[0]);

        if (installed is [var candidate])
        {
            var direct = await PowerShellProbe.RunWithCancellationAsync(cwd, token, executable: candidate).ConfigureAwait(false);
            var directory = Path.GetDirectoryName(candidate)!;
            if (!direct.VerifiedVersion)
                return BinaryFinding(direct, candidate);

            return CanAddToPath(directory)
                ? new("PathMissing", directory)
                : new("Unknown", Describe("PathEntryUnsupported", directory));
        }

        return HasInstallationFootprint()
            ? new("Unknown", "InstallationFootprintFound")
            : new("Missing", null);
    }

    private static DiagnosticResult ClassifyResolvedCommand(PowerShellProbe.Result result)
    {
        if (result.VerifiedVersion)
        {
            return result.LoggedIn switch
            {
                true => new("Ready", result.Path),
                false => new("AuthRequired", result.Path),
                null => new("Unknown", Describe(result.Issue, result.Path))
            };
        }

        var path = result.Path;
        var desktopAlias = path is not null && SamePath(path, LocalAppPath("Microsoft", "WindowsApps", "claude.exe"));
        if (desktopAlias || result.Kind is "Alias" or "Function" or "Cmdlet")
            return new("Conflict", path);
        if (path is not null && (result.BinaryCode is not null ||
            string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase)))
            return BinaryFinding(result, path);

        var otherInstallation = path is not null && File.Exists(path) && KnownExecutables().Any(
            candidate => File.Exists(candidate) && !SamePath(ExecutableTarget(candidate), ExecutableTarget(path)));
        return new(otherInstallation ? "Conflict" : "Unknown", Describe(result.Issue, path));
    }

    private static DiagnosticResult BinaryFinding(PowerShellProbe.Result result, string path) =>
        new(result.BinaryCode ?? "BinaryTrustUnknown",
            result.SignatureStatus is { Length: > 0 } status ? $"{path}\n{status}" : path);

    private static string? Describe(string? issue, string? path) =>
        issue is null ? path : path is null ? issue : $"{issue}\n{path}";

    internal static string UserWinGetClaude => LocalAppPath("Microsoft", "WinGet", "Packages",
        PortablePackageDirectory, "claude.exe");

    internal static string MachineWinGetClaude => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WinGet", "Packages",
        PortablePackageDirectory, "claude.exe");

    internal static string UserWinGetExecutable => LocalAppPath("Microsoft", "WindowsApps", "winget.exe");

    internal static async Task<DiagnosticResult> InstallAsync(string cwd, bool reinstall = false,
        CancellationToken token = default)
    {
        if (!await CanStartInstallerAsync(cwd, token).ConfigureAwait(false))
            return InstallFailure("NativeInstallerVerificationFailed");
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var result = await NativeInstaller.InstallWithFallbackAsync(
            ct => NativeInstaller.RunMonitoredAsync(NativeInstallCommand, profile, InstallerTimeout, TimeSpan.FromSeconds(2), ct),
            (afterStall, ct) => VerifyNativeInstallAsync(cwd, profile, afterStall, ct),
            ct => InstallWinGetAsync(cwd, reinstall, ct), token).ConfigureAwait(false);
        if (reinstall || result.Code != "Ready")
            return result;

        var current = await InspectAsync(cwd, token).ConfigureAwait(false);
        return current.Code == "StalePath" && current.Detail is { } directory
            ? await ActivatePathAsync(cwd, directory, persist: false).ConfigureAwait(false)
            : current;
    }

    private static async Task<DiagnosticResult?> VerifyNativeInstallAsync(string cwd, string profile,
        bool afterStall, CancellationToken token)
    {
        var native = Path.Combine(profile, ".local", "bin", "claude.exe");
        try
        {
            _ = File.GetAttributes(native);
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            // A different installer might have completed while the native installer was stalled.
            if (afterStall)
            {
                var current = await InspectAsync(cwd, token).ConfigureAwait(false);
                if (current.Code is "Ready" or "AuthRequired" or "PathMissing" or "StalePath" or "Conflict")
                    return current;
                if (current.Code != "Missing" && !(current.Code == "Unknown" && current.Detail == "InstallationFootprintFound"))
                    return InstallFailure("NativeInstallerVerificationFailed");
            }
            return null;
        }

        var verified = await PowerShellProbe.RunWithCancellationAsync(cwd, token, executable: native).ConfigureAwait(false);
        return verified.VerifiedVersion ? new("Ready", native) : InstallFailure("NativeInstallerVerificationFailed");
    }

    private static async Task<DiagnosticResult> InstallWinGetAsync(string cwd, bool reinstall,
        CancellationToken token)
    {
        if (!await CanStartInstallerAsync(cwd, token).ConfigureAwait(false))
            return InstallFailure("WinGetVerificationFailed");
        var executable = UserWinGetExecutable;
        if (!File.Exists(executable))
            return InstallFailure($"WinGetUnavailable\n{executable}");

        if (IsWinGetRunningInCurrentSession())
            return InstallFailure("WinGetAlreadyRunning");

        var startInfo = new ProcessStartInfo(executable)
        {
            WorkingDirectory = Environment.SystemDirectory,
            CreateNoWindow = true,
            UseShellExecute = false
        };

        foreach (var argument in new[] { "install", "--id", PackageId, "--exact", "--source", "winget",
            "--scope", "user", "--installer-type", "portable", reinstall ? "--force" : "--no-upgrade", "--disable-interactivity" })
        {
            startInfo.ArgumentList.Add(argument);
        }

        int exitCode;
        try
        {
            token.ThrowIfCancellationRequested();
            using var installer = Process.Start(startInfo);
            if (installer is null)
                return InstallFailure("WinGetStartFailed");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(InstallerTimeout);
            try
            {
                await installer.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Keep the repair mutex until WinGet exits or the bounded exit check finishes.
                if (!await StopInstallerAsync(installer).ConfigureAwait(false))
                    return InstallFailure("WinGetTimeoutTerminationUnverified");

                token.ThrowIfCancellationRequested();
                var installedPath = KnownExecutables().FirstOrDefault(File.Exists);
                return InstallFailure(installedPath is null ? "WinGetTimeout" : $"WinGetTimeout\n{installedPath}");
            }
            exitCode = installer.ExitCode;
        }
        catch (Win32Exception exception)
        {
            return InstallFailure($"WinGetStartFailed (Win32 {exception.NativeErrorCode})");
        }

        if (exitCode != 0)
            return InstallFailure(DescribeWinGetExitCode(exitCode));

        // The package manager may report success even if the command on PATH belongs to another installation.
        var installed = await PowerShellProbe.RunWithCancellationAsync(cwd, token, executable: UserWinGetClaude)
            .ConfigureAwait(false);
        return installed.VerifiedVersion ? new("Ready", UserWinGetClaude) : InstallFailure("WinGetVerificationFailed");
    }

    internal static bool IsWinGetRunningInCurrentSession()
    {
        using var current = Process.GetCurrentProcess();
        var session = current.SessionId;
        var candidates = Process.GetProcessesByName("winget");
        try
        {
            return candidates.Any(candidate => candidate.SessionId == session && !candidate.HasExited);
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            // A visible WinGet process that cannot be inspected may still be installing.
            return true;
        }
        finally
        {
            foreach (var candidate in candidates)
                candidate.Dispose();
        }
    }

    private static async Task<bool> StopInstallerAsync(Process installer)
    {
        var stopRequested = true;
        try
        {
            if (!installer.HasExited)
                installer.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or AggregateException)
        {
            stopRequested = false;
        }

        using var timeout = new CancellationTokenSource(InstallerExitTimeout);
        try
        {
            // Exit observation applies to the WinGet process itself. Descendant termination is best effort.
            await installer.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return stopRequested;
        }
        catch (Exception exception) when (exception is OperationCanceledException or Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    private static async Task<bool> CanStartInstallerAsync(string cwd, CancellationToken token)
    {
        var current = await InspectAsync(cwd, token).ConfigureAwait(false);
        return (current.Code == "Missing" || current is { Code: "Unknown", Detail: "InstallationFootprintFound" }) &&
            InstallChecks.CanInstallIntoEmptyTargets();
    }

    private static DiagnosticResult InstallFailure(string evidence) => new("RepairFailed", $"{evidence}\n{OfficialInstallHelp}");

    private static string DescribeWinGetExitCode(int exitCode)
    {
        const string interactive = "winget install --id " + PackageId + " --exact --source winget --scope user";
        var code = unchecked((uint)exitCode);
        return code switch
        {
            // These HRESULTs identify the agreement WinGet needs the user to review.
            0x8A150041 => $"WinGetPackageConsentRequired (0x{code:X8})\n{interactive}",
            0x8A150046 => $"WinGetSourceConsentRequired (0x{code:X8})\n{interactive}",
            _ => $"WinGetInstallFailed (0x{code:X8})"
        };
    }

    private static async Task<DiagnosticResult> ActivatePathAsync(string cwd, string directory, bool persist)
    {
        var livePath = Environment.GetEnvironmentVariable("Path") ?? "";
        var newPath = AppendDirectory(livePath, directory);
        var probe = await PowerShellProbe.RunAsync(cwd, pathOverride: newPath).ConfigureAwait(false);
        if (probe is not { VerifiedVersion: true, Path: not null } ||
            !SamePath(Path.GetDirectoryName(probe.Path)!, directory))
        {
            return new("RepairFailed", Describe(probe.Issue ?? "PathTargetChanged", directory));
        }

        if (persist)
        {
            // Preserve the raw entry text and registry value kind, including expandable entries.
            using var environment = Registry.CurrentUser.CreateSubKey(UserEnvironmentKey);
            var (raw, kind) = ReadPathValue(environment);
            if (!ContainsDirectory(ExpandPath(raw, kind), directory))
            {
                if (!CanAddToPath(directory))
                    return new("RepairFailed", Describe("PathEntryUnsupported", directory));

                var replacement = AppendPath(raw, directory);
                if (ReadPathValue(environment) != (raw, kind))
                    return new("RepairFailed", Describe("UserPathChanged", directory));

                RepairJournal.PreparePaths(directory, replacement, kind, newPath);
                if (ReadPathValue(environment) != (raw, kind))
                    return new("RepairFailed", Describe("UserPathChanged", directory));
                environment.SetValue("Path", replacement, kind);
                if (ReadPathValue(environment) != (replacement, kind))
                    return new("RepairFailed", Describe("UserPathChanged", directory));

                SendMessageTimeout(new IntPtr(0xffff), 0x001a, UIntPtr.Zero, "Environment", 0x0002, 500, out _);
            }
        }

        if (!persist)
            RepairJournal.PreparePaths(directory, null, RegistryValueKind.String, newPath);
        // Child terminals inherit this app's environment even when Explorer has a newer PATH.
        Environment.SetEnvironmentVariable("Path", newPath, EnvironmentVariableTarget.Process);
        var verified = await InspectAsync(cwd).ConfigureAwait(false);
        return verified.Code is "Missing" or "PathMissing" or "StalePath"
            ? new("RepairFailed", Describe("PathVerificationFailed", directory))
            : verified;
    }

    private static string[] KnownExecutables() =>
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "claude.exe"),
            LocalAppPath("Microsoft", "WinGet", "Links", "claude.exe"),
            LocalAppPath("Microsoft", "WinGet", "Packages", PortablePackageDirectory, "claude.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WinGet", "Links", "claude.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "WinGet", "Packages", PortablePackageDirectory, "claude.exe")
        ];

    private static string LocalAppPath(params string[] parts) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Path.Combine(parts));

    private static string ExecutableTarget(string path) => File.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName ?? path;

    private static bool HasInstallationFootprint()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var npm = Path.Combine(roaming, "npm");
        var versions = Path.Combine(profile, ".local", "share", "claude", "versions");
        var native = Path.Combine(profile, ".local", "bin");

        return File.Exists(Path.Combine(npm, "node_modules", "@anthropic-ai", "claude-code", "package.json")) ||
            File.Exists(Path.Combine(npm, "claude.cmd")) || File.Exists(Path.Combine(npm, "claude.ps1")) ||
            File.Exists(LocalAppPath("Microsoft", "WindowsApps", "claude.exe")) ||
            (Directory.Exists(native) && Directory.EnumerateFiles(native, "claude.exe.*").Any()) ||
            (Directory.Exists(versions) && Directory.EnumerateFiles(versions).Any()) ||
            IsRegistered(Registry.CurrentUser) || IsRegistered(Registry.LocalMachine);
    }

    private static bool IsRegistered(RegistryKey root)
    {
        using var uninstall = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
        return uninstall?.GetSubKeyNames().Any(name => name.Equals(PackageId, StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith(PackageId + "_", StringComparison.OrdinalIgnoreCase)) == true;
    }

    private static string ReadStoredPath()
    {
        using var machine = Registry.LocalMachine.OpenSubKey(MachineEnvironmentKey);
        using var user = Registry.CurrentUser.OpenSubKey(UserEnvironmentKey);
        var (machinePath, machineKind) = ReadPathValue(machine);
        var (userPath, userKind) = ReadPathValue(user);
        return AppendPath(ExpandPath(machinePath, machineKind), ExpandPath(userPath, userKind));
    }

    private static (string Value, RegistryValueKind Kind) ReadPathValue(RegistryKey? key) =>
        key?.GetValue("Path", null, RegistryValueOptions.DoNotExpandEnvironmentNames) switch
        {
            null => ("", RegistryValueKind.ExpandString),
            string value => (value, key.GetValueKind("Path")),
            _ => throw new InvalidOperationException()
        };

    private static string ExpandPath(string value, RegistryValueKind kind) =>
        kind == RegistryValueKind.ExpandString ? Environment.ExpandEnvironmentVariables(value) : value;

    private static bool CanAddToPath(string directory) => !directory.Contains(';') && !directory.Contains('%');

    private static bool ContainsDirectory(string path, string directory) => path.Split(';').Any(
        entry => SamePath(entry.Trim().Trim('"'), directory));

    private static bool SamePath(string first, string second) => string.Equals(
        Path.TrimEndingDirectorySeparator(first), Path.TrimEndingDirectorySeparator(second), StringComparison.OrdinalIgnoreCase);

    private static string AppendDirectory(string path, string directory) =>
        ContainsDirectory(path, directory) ? path : AppendPath(path, directory);

    private static string AppendPath(string left, string right) => (left.Length, right.Length) switch
    {
        (0, _) => right,
        (_, 0) => left,
        _ => left.EndsWith(';') ? left + right : left + ";" + right
    };

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageTimeoutW", ExactSpelling = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, UIntPtr wParam,
        string lParam, uint flags, uint timeout, out UIntPtr result);
}
