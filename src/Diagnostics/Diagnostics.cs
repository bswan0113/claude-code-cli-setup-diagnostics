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
    private const string PortablePackageDirectory = PackageId + "_Microsoft.Winget.Source_8wekyb3d8bbwe";
    internal const string OfficialInstallHelp = "https://code.claude.com/docs/en/setup#install-on-native-windows";
    private const string UserEnvironmentKey = "Environment";
    private const string MachineEnvironmentKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment";
    private static readonly TimeSpan InstallerTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan InstallerExitTimeout = TimeSpan.FromSeconds(5);

    public static Task<DiagnosticResult> CheckAsync(string cwd) =>
        Task.Run(() => RunAsync(cwd, repairCode: null, expectedDetail: null));

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

    private static async Task<DiagnosticResult> RunAsync(string cwd, string? repairCode, string? expectedDetail)
    {
        if (string.IsNullOrWhiteSpace(cwd) || !Path.IsPathFullyQualified(cwd) || !Directory.Exists(cwd))
            return new("InvalidFolder", null);

        try
        {
            var current = await InspectAsync(cwd).ConfigureAwait(false);
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
                "Missing" => await InstallAsync(cwd).ConfigureAwait(false),
                "PathMissing" when current.Detail is { } directory =>
                    await ActivatePathAsync(cwd, directory, persist: true).ConfigureAwait(false),
                "StalePath" when current.Detail is { } directory =>
                    await ActivatePathAsync(cwd, directory, persist: false).ConfigureAwait(false),
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

    private static async Task<DiagnosticResult> InspectAsync(string cwd)
    {
        // Match the launcher first: Windows PowerShell without a profile, using the app process's PATH.
        var live = await PowerShellProbe.RunAsync(cwd, checkAuth: true).ConfigureAwait(false);
        if (live.Kind != "Absent")
            return ClassifyResolvedCommand(live);

        var storedPath = ReadStoredPath();
        if (!string.Equals(storedPath, Environment.GetEnvironmentVariable("Path"), StringComparison.Ordinal))
        {
            var fresh = await PowerShellProbe.RunAsync(cwd, pathOverride: storedPath).ConfigureAwait(false);
            if (fresh is { VerifiedVersion: true, Path: not null })
                return new("StalePath", Path.GetDirectoryName(fresh.Path));

            if (fresh.Kind != "Absent")
                return ClassifyResolvedCommand(fresh);
        }

        var installed = KnownExecutables().Where(File.Exists)
            .DistinctBy(ExecutableTarget, StringComparer.OrdinalIgnoreCase).ToArray();
        if (installed.Length > 1)
            return new("Conflict", installed[0]);

        if (installed is [var candidate])
        {
            var direct = await PowerShellProbe.RunAsync(cwd, executable: candidate).ConfigureAwait(false);
            var directory = Path.GetDirectoryName(candidate)!;
            if (!direct.VerifiedVersion)
                return new("Unknown", Describe(direct.Issue, candidate));

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

        var otherInstallation = path is not null && File.Exists(path) && KnownExecutables().Any(
            candidate => File.Exists(candidate) && !SamePath(ExecutableTarget(candidate), ExecutableTarget(path)));
        return new(otherInstallation ? "Conflict" : "Unknown", Describe(result.Issue, path));
    }

    private static string? Describe(string? issue, string? path) =>
        issue is null ? path : path is null ? issue : $"{issue}\n{path}";

    private static async Task<DiagnosticResult> InstallAsync(string cwd)
    {
        var executable = LocalAppPath("Microsoft", "WindowsApps", "winget.exe");
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
            "--scope", "user", "--installer-type", "portable", "--no-upgrade", "--disable-interactivity" })
        {
            startInfo.ArgumentList.Add(argument);
        }

        int exitCode;
        try
        {
            using var installer = Process.Start(startInfo);
            if (installer is null)
                return InstallFailure("WinGetStartFailed");

            using var timeout = new CancellationTokenSource(InstallerTimeout);
            try
            {
                await installer.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Keep the repair mutex until WinGet exits or the bounded exit check finishes.
                if (!await StopInstallerAsync(installer).ConfigureAwait(false))
                    return InstallFailure("WinGetTimeoutTerminationUnverified");

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

        var verified = await InspectAsync(cwd).ConfigureAwait(false);
        if (verified.Code == "StalePath" && verified.Detail is { } directory)
            return await ActivatePathAsync(cwd, directory, persist: false).ConfigureAwait(false);

        // An installer exit code is insufficient to establish the final installation state.
        return verified.Code == "Missing" ? InstallFailure("WinGetVerificationFailed") : verified;
    }

    private static bool IsWinGetRunningInCurrentSession()
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

                environment.SetValue("Path", replacement, kind);
                if (ReadPathValue(environment) != (replacement, kind))
                    return new("RepairFailed", Describe("UserPathChanged", directory));

                SendMessageTimeout(new IntPtr(0xffff), 0x001a, UIntPtr.Zero, "Environment", 0x0002, 500, out _);
            }
        }

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
