using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

namespace ClaudeLauncher;

internal static partial class InstallChecks
{
    private const string UserEnvironment = "Environment";
    private const string MachineEnvironment = @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment";
    private const string DisableUpdater = "DISABLE_AUTOUPDATER";
    private const string ManagerUpdater = "CLAUDE_CODE_PACKAGE_MANAGER_AUTO_UPDATE";

    private sealed record RegistryPath(string Raw, RegistryValueKind Kind)
    {
        public string Expanded => Kind == RegistryValueKind.ExpandString ? Environment.ExpandEnvironmentVariables(Raw) : Raw;
    }

    private sealed record PathState(RegistryPath Machine, RegistryPath User, string Process)
    {
        public string Stored => CombinePath(Machine.Expanded, User.Expanded);
    }

    private sealed record CommandHit(string Kind, string? Path);
    private sealed record PathReport(CommandHit[] Live, CommandHit[] Stored, CommandHit[] ProposedStored,
        CommandHit[] ProposedLive);

    private const string FindCommands = """
        $ErrorActionPreference = 'Stop'
        Microsoft.PowerShell.Management\Set-Location -LiteralPath $env:BLSLAB_CLAUDE_INSTALL_CHECK_FOLDER
        function Find-LauncherClaude($searchPath) {
            $env:Path = $searchPath
            Microsoft.PowerShell.Core\Get-Command -Name claude -ListImported -All -ErrorAction SilentlyContinue |
                Microsoft.PowerShell.Core\ForEach-Object {
                    [pscustomobject]@{ Kind = [string]$_.CommandType; Path = $_.Path }
                }
        }
        $live = @(Find-LauncherClaude $env:BLSLAB_CLAUDE_INSTALL_PATH_LIVE)
        $stored = @(if ($env:BLSLAB_CLAUDE_INSTALL_PATH_STORED -ceq $env:BLSLAB_CLAUDE_INSTALL_PATH_LIVE) {
            $live
        } else { Find-LauncherClaude $env:BLSLAB_CLAUDE_INSTALL_PATH_STORED })
        $proposedStored = @(if ($env:BLSLAB_CLAUDE_INSTALL_PATH_NEXT_STORED -ceq $env:BLSLAB_CLAUDE_INSTALL_PATH_STORED) {
            $stored
        } else { Find-LauncherClaude $env:BLSLAB_CLAUDE_INSTALL_PATH_NEXT_STORED })
        $proposedLive = @(if ($env:BLSLAB_CLAUDE_INSTALL_PATH_NEXT_LIVE -ceq $env:BLSLAB_CLAUDE_INSTALL_PATH_NEXT_STORED) {
            $proposedStored
        } else { Find-LauncherClaude $env:BLSLAB_CLAUDE_INSTALL_PATH_NEXT_LIVE })
        Microsoft.PowerShell.Utility\ConvertTo-Json -InputObject @{
            Live = @($live); Stored = @($stored); ProposedStored = @($proposedStored); ProposedLive = @($proposedLive)
        } -Compress
        """;

    private static async Task AddPathChecksAsync(List<Plan> plans, InstallPaths paths, PreferredInstall? preferred,
        string cwd, CancellationToken token)
    {
        var state = ReadPaths();
        var user = preferred is not null && CanUsePathEntry(preferred.PathEntry)
            ? PutFirst(state.User, preferred.PathEntry) : state.User;
        var storedCandidate = CombinePath(state.Machine.Expanded, user.Expanded);
        var liveCandidate = preferred is not null && CanUsePathEntry(preferred.PathEntry)
            ? PutProcessEntryFirst(state, preferred.PathEntry) : state.Process;
        var report = await ReadCommandPathsAsync(cwd, state, storedCandidate, liveCandidate, token).ConfigureAwait(false);
        if (report is null)
        {
            plans.Add(new(new(3, "InstallPathUnknown", CheckState.Unknown)));
            plans.Add(new(new(4, "InstallCommandUnknown", CheckState.Unknown)));
            return;
        }

        if (preferred is null)
        {
            plans.Add(new(new(3, "InstallPathDeferred", CheckState.Info)));
        }
        else
        {
            var liveMatches = UsesPreferred(report.Live, preferred);
            var savedMatches = UsesPreferred(report.Stored, preferred);
            var registered = savedMatches || ContainsEntry(state.Stored, preferred.PathEntry);
            var canPrioritize = user != state.User && UsesPreferred(report.ProposedStored, preferred) &&
                UsesPreferred(report.ProposedLive, preferred);
            var evidence = preferred.PathEntry;
            if (savedMatches && !liveMatches && UsesPreferred(report.ProposedLive, preferred))
            {
                plans.Add(Mutation(3, "InstallPathStale", CheckState.Warning, evidence, "RefreshClaudeProcessPath",
                    JoinLines(["Claude Launcher process PATH", preferred.PathEntry]), Fingerprint(state),
                    cancellation => ApplyPathAsync(state, state.User, preferred, cwd, persist: false, cancellation)));
            }
            else if (savedMatches && !liveMatches)
            {
                plans.Add(new(new(3, "InstallPathStale", CheckState.Warning, evidence)));
            }
            else if (!registered && canPrioritize)
            {
                plans.Add(Mutation(3, "InstallPathMissing", CheckState.Warning, evidence, "PreferClaudePath",
                    PathTarget(preferred), Fingerprint(state, user),
                    cancellation => ApplyPathAsync(state, user, preferred, cwd, persist: true, cancellation)));
            }
            else
            {
                plans.Add(new(new(3, registered ? "InstallPathReady" : "InstallPathMissing",
                    registered ? CheckState.Pass : CheckState.Warning, evidence)));
            }

            if (registered && !savedMatches && canPrioritize)
            {
                plans.Add(Mutation(4, "InstallPathPriority", CheckState.Warning, evidence, "PreferClaudePath",
                    PathTarget(preferred), Fingerprint(state, user),
                    cancellation => ApplyPathAsync(state, user, preferred, cwd, persist: true, cancellation)));
            }
            else if (!savedMatches && report.Stored.Length > 0 && !canPrioritize)
            {
                plans.Add(new(new(4, "InstallPathShadowed", CheckState.Warning,
                    JoinLines(new[] { report.Stored[0].Path ?? report.Stored[0].Kind, preferred.PathEntry }))));
            }
        }

        var ordered = report.Live.DistinctBy(hit => hit.Path ?? hit.Kind, PathComparer).ToArray();
        var manualAlias = ordered.FirstOrDefault()?.Path is { } first &&
            PathComparer.Equals(first, Path.Combine(paths.LocalAppData, "Microsoft", "WindowsApps", "claude.exe"));
        var key = ordered.Length == 0 ? "InstallCommandAbsent" : manualAlias ? "InstallDesktopAlias" : "InstallCommandOrder";
        plans.Add(new(new(4, key, ordered.Length == 0 ? CheckState.Info : ordered.Length > 1 || manualAlias
                ? CheckState.Warning : CheckState.Pass,
            JoinLines(ordered.Select((hit, index) => $"{index + 1}. {hit.Path ?? hit.Kind}")))));
        if (!PathComparer.Equals(state.Process, state.Stored) && !report.Live.SequenceEqual(report.Stored))
        {
            plans.Add(new(new(4, "InstallStoredCommandOrder", CheckState.Info,
                JoinLines(report.Stored.Select((hit, index) => $"{index + 1}. {hit.Path ?? hit.Kind}")))));
        }
    }

    private static async Task<PathReport?> ReadCommandPathsAsync(string cwd, PathState state, string storedCandidate,
        string liveCandidate, CancellationToken token)
    {
        var response = await RunPowerShellAsync(FindCommands, token, env =>
        {
            env["BLSLAB_CLAUDE_INSTALL_CHECK_FOLDER"] = cwd;
            env["BLSLAB_CLAUDE_INSTALL_PATH_LIVE"] = state.Process;
            env["BLSLAB_CLAUDE_INSTALL_PATH_STORED"] = state.Stored;
            env["BLSLAB_CLAUDE_INSTALL_PATH_NEXT_STORED"] = storedCandidate;
            env["BLSLAB_CLAUDE_INSTALL_PATH_NEXT_LIVE"] = liveCandidate;
        }).ConfigureAwait(false);
        try
        {
            return response is { ExitCode: 0, Output.Length: > 0 } &&
                JsonSerializer.Deserialize<PathReport>(response.Output) is
                    { Live: not null, Stored: not null, ProposedStored: not null, ProposedLive: not null } report
                ? report : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool UsesPreferred(CommandHit[] hits, PreferredInstall preferred) =>
        hits.FirstOrDefault() is { Kind: "Application", Path: not null } first &&
        PathComparer.Equals(ResolvedFile(first.Path), ResolvedFile(preferred.Executable));

    private static PathState ReadPaths()
    {
        using var machine = Registry.LocalMachine.OpenSubKey(MachineEnvironment);
        using var user = Registry.CurrentUser.OpenSubKey(UserEnvironment);
        return new(ReadRegistryPath(machine), ReadRegistryPath(user),
            Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.Process) ?? "");
    }

    private static RegistryPath ReadRegistryPath(RegistryKey? key) =>
        key?.GetValue("Path", null, RegistryValueOptions.DoNotExpandEnvironmentNames) switch
        {
            null => new("", RegistryValueKind.ExpandString),
            string value => new(value, key.GetValueKind("Path")),
            _ => throw new InvalidOperationException(),
        };

    private static bool CanUsePathEntry(string value) => !value.Contains('%') && !value.Contains(';');
    private static string CombinePath(string first, string second) =>
        first.Length == 0 ? second : second.Length == 0 || first.EndsWith(';') ? first + second : first + ";" + second;
    private static bool ContainsEntry(string path, string entry) =>
        path.Split(';').Any(value => EqualEntry(value, entry));
    private static bool EqualEntry(string raw, string entry) =>
        PathComparer.Equals(WorkingFolder.Normalize(raw.Trim().Trim('"')), entry);

    private static RegistryPath PutFirst(RegistryPath user, string entry)
    {
        var other = user.Raw.Length == 0 ? [] : user.Raw.Split(';').Where(value => !EqualEntry(
            user.Kind == RegistryValueKind.ExpandString ? Environment.ExpandEnvironmentVariables(value) : value, entry));
        return user with { Raw = string.Join(';', new[] { entry }.Concat(other)) };
    }

    private static string PathTarget(PreferredInstall preferred) =>
        JoinLines([@"HKCU\Environment\Path", preferred.PathEntry, "Claude Launcher process PATH"]);

    private static string PutProcessEntryFirst(PathState state, string entry) =>
        PutFirst(new RegistryPath(state.Process, RegistryValueKind.String), entry).Raw;

    private static async Task<bool> ApplyPathAsync(PathState expected, RegistryPath user, PreferredInstall preferred,
        string cwd, bool persist, CancellationToken token)
    {
        var processPath = PutProcessEntryFirst(expected, preferred.PathEntry);
        var savedPath = CombinePath(expected.Machine.Expanded, user.Expanded);
        if (!await VerifyPathAsync(cwd, processPath, preferred, token).ConfigureAwait(false) ||
            (persist && savedPath != processPath &&
                !await VerifyPathAsync(cwd, savedPath, preferred, token).ConfigureAwait(false)) ||
            ReadPaths() != expected)
        {
            return false;
        }

        token.ThrowIfCancellationRequested();
        if (persist)
        {
            using var key = Registry.CurrentUser.CreateSubKey(UserEnvironment);
            if (ReadRegistryPath(key) != expected.User)
                return false;
            key.SetValue("Path", user.Raw, user.Kind);
            if (ReadRegistryPath(key) != user)
                return false;
            BroadcastEnvironment();
        }

        Environment.SetEnvironmentVariable("Path", processPath, EnvironmentVariableTarget.Process);
        return Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.Process) == processPath;
    }

    private static async Task<bool> VerifyPathAsync(string cwd, string candidate, PreferredInstall preferred,
        CancellationToken token)
    {
        var probe = await PowerShellProbe.RunWithCancellationAsync(cwd, token, pathOverride: candidate).ConfigureAwait(false);
        return probe.VerifiedVersion && probe.Path is not null &&
            PathComparer.Equals(ResolvedFile(probe.Path), ResolvedFile(preferred.Executable));
    }

    private static string? ReadScope(string name, EnvironmentVariableTarget scope) => Environment.GetEnvironmentVariable(name, scope);
    private static bool IsEnabled(string? value) => value is not null &&
        (value.Equals("1", StringComparison.Ordinal) || value.Equals("true", StringComparison.OrdinalIgnoreCase));

    private sealed record UpdateFlags(string? User, string? Machine, string? Process, string? PackageManager);

    private static UpdateFlags GetUpdateFlags() => new(
        ReadScope(DisableUpdater, EnvironmentVariableTarget.User), ReadScope(DisableUpdater, EnvironmentVariableTarget.Machine),
        ReadScope(DisableUpdater, EnvironmentVariableTarget.Process), ReadScope(ManagerUpdater, EnvironmentVariableTarget.Process));

    private static void AddUpdaterCheck(List<Plan> plans, InstallPaths paths, BinaryState native, PreferredInstall? preferred,
        bool updaterArtifacts)
    {
        var flags = GetUpdateFlags();
        var disabled = IsEnabled(flags.User) ||
            flags.User is null && IsEnabled(flags.Machine) || IsEnabled(flags.Process);
        if (disabled)
        {
            plans.Add(new(new(8, "InstallUpdaterDisabled", CheckState.Info, DisableUpdater)));
        }
        else if (native.File is not null && (!native.Valid || updaterArtifacts))
        {
            const string target = "HKCU\\Environment\\DISABLE_AUTOUPDATER = 1\nClaude Launcher process environment";
            plans.Add(Mutation(8, "InstallUpdaterRisk", CheckState.Warning, DisableUpdater, "DisableClaudeUpdater", target,
                Fingerprint(flags, native.File, updaterArtifacts), token =>
                {
                    token.ThrowIfCancellationRequested();
                    if (GetUpdateFlags() != flags || Stamp(paths.Native) != native.File)
                        return Task.FromResult(false);
                    Environment.SetEnvironmentVariable(DisableUpdater, "1", EnvironmentVariableTarget.User);
                    Environment.SetEnvironmentVariable(DisableUpdater, "1", EnvironmentVariableTarget.Process);
                    return Task.FromResult(ReadScope(DisableUpdater, EnvironmentVariableTarget.User) == "1");
                }));
        }
        else if (preferred is not null && (PathComparer.Equals(preferred.Executable, paths.UserWinGet) ||
            PathComparer.Equals(preferred.Executable, paths.MachineWinGet)))
        {
            plans.Add(new(new(8, IsEnabled(flags.PackageManager) ? "InstallUpdaterWinGetEnabled" : "InstallUpdaterWinGet",
                CheckState.Info, "winget upgrade Anthropic.ClaudeCode")));
        }
        else
        {
            plans.Add(new(new(8, "InstallUpdaterStatus", CheckState.Info, DisableUpdater)));
        }
    }

    private static void BroadcastEnvironment() => SendMessageTimeout(new IntPtr(0xffff), 0x001a,
        UIntPtr.Zero, "Environment", 0x0002, 500, out _);

    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, UIntPtr wParam, string parameter,
        uint flags, uint timeout, out UIntPtr result);
}
