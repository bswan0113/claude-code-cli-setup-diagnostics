using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;

namespace ClaudeLauncher;

internal enum CheckState { Pass, Info, Warning, Failed, Unknown }

internal sealed record CheckItem(int Stage, string Key, CheckState State, string Evidence = "",
    string? Action = null, string? Target = null)
{
    // Used for checking that the approval still refers to the same value. Never display it.
    public string? Revision { get; init; }
    public SettingsLocation? SettingsError { get; init; }
    public bool ManualNativeRecovery { get; init; }
    internal string ProfileConfigRoot { get; init; } = ProfileDataPaths.Config;
}

internal sealed record RepairOutcome(bool Applied, DiagnosticResult? Failure = null);

internal static class FullDiagnostics
{
    public static async Task<IReadOnlyList<CheckItem>> ScanAsync(string cwd, CancellationToken token)
    {
        using var scope = LauncherProfiles.Bind(LauncherProfiles.Current);
        token.ThrowIfCancellationRequested();
        var system = Task.Run(() => InspectComputerAsync(token), token);
        var install = ScanGroupAsync(InstallChecks.ScanAsync, cwd, [2, 3, 4, 8], token);
        var environment = ScanGroupAsync(EnvironmentChecks.ScanAsync, cwd, [5, 6, 7], token);
        var settings = ScanGroupAsync(SettingsDiagnostics.ScanAsync, cwd, [6], token);
        var defender = InspectMissingBinaryAsync(install, token);
        await Task.WhenAll(system, install, environment, settings, defender).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        return system.Result.Concat(install.Result).Concat(environment.Result).Concat(settings.Result).Concat(defender.Result)
            .OrderBy(item => item.Stage).ToArray();
    }

    private static async Task<CheckItem[]> InspectMissingBinaryAsync(Task<IReadOnlyList<CheckItem>> installation,
        CancellationToken token)
    {
        if (!(await installation.ConfigureAwait(false)).Any(item => item.Key == "InstallBinaryMissing"))
            return [];
        return [await DefenderDiagnostics.InspectMissingAsync(token).ConfigureAwait(false)];
    }

    private static async Task<IReadOnlyList<CheckItem>> ScanGroupAsync(
        Func<string, CancellationToken, Task<IReadOnlyList<CheckItem>>> scan,
        string cwd, int[] stages, CancellationToken token)
    {
        try
        {
            return await Task.Run(() => scan(cwd, token), token).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsInspectionError(ex))
        {
            return stages.Select(stage => new CheckItem(stage, "InspectionError", CheckState.Unknown,
                ex.GetType().Name)).ToArray();
        }
    }

    private static bool IsInspectionError(Exception ex) => ex is IOException or UnauthorizedAccessException or
        SecurityException or Win32Exception or InvalidOperationException or ArgumentException or NotSupportedException
        or System.Text.Json.JsonException;

    private static async Task<IReadOnlyList<CheckItem>> InspectComputerAsync(CancellationToken token)
    {
        var is64 = Environment.Is64BitOperatingSystem;
        var os = new CheckItem(1, is64 ? "Windows64" : "Windows32", is64 ? CheckState.Pass : CheckState.Failed,
            $"{RuntimeInformation.OSDescription} · {RuntimeInformation.OSArchitecture}");
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var special = profile.Any(c => c > 127 || c == ' ');
        var path = new CheckItem(1, special ? "ProfileCharacters" : "ProfilePath",
            special ? CheckState.Info : CheckState.Pass, profile);
        CheckItem shell;
        var executable = PowerShellProbe.Executable;
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            WorkingDirectory = Environment.SystemDirectory
        };
        LauncherProfiles.ApplyCliEnvironment(start.Environment);
        foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command",
            "$PSVersionTable.PSVersion.ToString()" })
            start.ArgumentList.Add(argument);
        try
        {
            using var process = Process.Start(start) ?? throw new InvalidOperationException();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            try
            {
                var output = await process.StandardOutput.ReadToEndAsync(timeout.Token).ConfigureAwait(false);
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                shell = process.ExitCode == 0 && Version.TryParse(output.Trim(), out var version)
                    ? new CheckItem(1, "ShellInstalled", CheckState.Pass, $"Windows PowerShell {version}\n{executable}")
                    : new CheckItem(1, "ShellUnknown", CheckState.Unknown, executable);
            }
            catch (OperationCanceledException)
            {
                try { if (!process.HasExited) process.Kill(); }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { }
                token.ThrowIfCancellationRequested();
                shell = new CheckItem(1, "ShellUnknown", CheckState.Unknown, executable);
            }
        }
        catch (Exception ex) when (IsInspectionError(ex))
        {
            shell = new CheckItem(1, "ShellUnknown", CheckState.Unknown, ex.GetType().Name);
        }

        return [os, shell, path];
    }

    public static async Task<bool> ApplyAsync(string cwd, CheckItem approved, CancellationToken token) =>
        (await ApplyWithOutcomeAsync(cwd, approved, token).ConfigureAwait(false)).Applied;

    internal static Task<RepairOutcome> ApplyWithOutcomeAsync(string cwd, CheckItem approved, CancellationToken token)
    {
        if (approved.Action is null || approved.Revision is null || string.IsNullOrWhiteSpace(approved.Target) ||
            WorkingFolder.Normalize(cwd) is null || !Directory.Exists(cwd))
            return Task.FromResult(new RepairOutcome(false));

        return LauncherProfiles.RunBound(() =>
        {
            if (!string.Equals(approved.ProfileConfigRoot, ProfileDataPaths.Config, StringComparison.OrdinalIgnoreCase))
                return new RepairOutcome(false);
            token.ThrowIfCancellationRequested();
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                if (identity.User is not { } user)
                    return new RepairOutcome(false);

                // Hold the same per-user lock as the quick installation and PATH repairs.
                // Mutex ownership stays on this worker thread throughout the asynchronous repair.
                using var mutex = new Mutex(false, $@"Global\BlsLab.ClaudeLauncher.Repair.{user.Value}");
                try
                {
                    if (!mutex.WaitOne(0))
                        return new RepairOutcome(false);
                }
                catch (AbandonedMutexException) { }

                try
                {
                    token.ThrowIfCancellationRequested();
                    return RepairJournal.TrackAsync(approved.Action,
                        () => DispatchAsync(cwd, approved, token), result => result.Applied,
                        JournalSubject(approved)).GetAwaiter().GetResult();
                }
                finally { mutex.ReleaseMutex(); }
            }
            catch (Exception ex) when (IsInspectionError(ex) || ex is WaitHandleCannotBeOpenedException)
            {
                return new RepairOutcome(false);
            }
        }, token);
    }

    private static string? JournalSubject(CheckItem approved)
    {
        if (approved.Action == SettingsDiagnostics.RestoreAction)
            return Path.GetFileName(approved.Evidence) == ".claude.json" ? "~/.claude.json" : "~/.claude/settings.json";
        if (approved.Action is "ClearUserVariable" or "SetUserGitBash")
            return approved.Evidence.Split(' ', '\n', '\r')[0];
        return null;
    }

    private static async Task<RepairOutcome> DispatchAsync(string cwd, CheckItem approved, CancellationToken token)
    {
        if (approved.Action == SettingsDiagnostics.RestoreAction && approved.Stage == 6)
            return new(await SettingsDiagnostics.ApplyAsync(cwd, approved, token).ConfigureAwait(false));
        return approved.Stage switch
        {
            2 or 3 or 4 or 8 => await InstallChecks.ApplyWithOutcomeAsync(cwd, approved, token).ConfigureAwait(false),
            5 or 6 => new(await EnvironmentChecks.ApplyAsync(cwd, approved, token).ConfigureAwait(false)),
            _ => new(false)
        };
    }
}
