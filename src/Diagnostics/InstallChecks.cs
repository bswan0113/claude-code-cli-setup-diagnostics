using System.ComponentModel;
using System.Globalization;
using System.Security;
using System.Security.Cryptography;

namespace ClaudeLauncher;

internal static partial class InstallChecks
{
    private const string Package = "@anthropic-ai/claude-code";
    private const long ExpectedNativeSize = 50 * 1024 * 1024;
    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;

    internal static async Task<IReadOnlyList<CheckItem>> ScanAsync(string cwd, CancellationToken token) =>
        (await InspectAsync(cwd, token).ConfigureAwait(false)).Select(plan => plan.Item).ToArray();

    internal static async Task<RepairOutcome> ApplyWithOutcomeAsync(string cwd, CheckItem approved, CancellationToken token)
    {
        if (approved.Action is null || approved.Target is null || approved.Revision is null)
            return new(false);

        try
        {
            var plans = await InspectAsync(cwd, token).ConfigureAwait(false);
            var current = plans.SingleOrDefault(plan => plan.Item.Stage == approved.Stage &&
                plan.Item.Key == approved.Key && plan.Item.Action == approved.Action &&
                plan.Item.Target == approved.Target && plan.Item.Revision == approved.Revision);
            token.ThrowIfCancellationRequested();
            if (current?.Install is { } install)
            {
                var result = await install(token).ConfigureAwait(false);
                return result.Code == "RepairFailed" ? new(false, result) : new(result.Code == "Ready");
            }
            return new(current?.Apply is { } apply && await apply(token).ConfigureAwait(false));
        }
        catch (Exception error) when (IsInspectionError(error))
        {
            return new(false);
        }
    }

    private static async Task<IReadOnlyList<Plan>> InspectAsync(string cwd, CancellationToken token)
    {
        var plans = new List<Plan>();
        var paths = new InstallPaths();
        var probeFolder = WorkingFolder.Normalize(cwd) is { } selected && Directory.Exists(selected)
            ? selected : Environment.SystemDirectory;

        var native = await InspectBinaryAsync(paths.Native, token).ConfigureAwait(false);
        var userWinGet = !native.Valid
            ? await InspectBinaryAsync(paths.UserWinGet, token).ConfigureAwait(false) : default;
        var machineWinGet = !native.Valid && !userWinGet.Valid
            ? await InspectBinaryAsync(paths.MachineWinGet, token).ConfigureAwait(false) : default;
        var alternate = userWinGet.Valid ? userWinGet : machineWinGet;
        var preferred = native.Valid ? new PreferredInstall(paths.Native, paths.Bin)
            : userWinGet.Valid ? WinGetPreferred(paths.UserWinGet, paths.UserWinGetLink)
            : machineWinGet.Valid ? WinGetPreferred(paths.MachineWinGet, paths.MachineWinGetLink)
            : null;
        var resolved = await PowerShellProbe.RunWithCancellationAsync(probeFolder, token).ConfigureAwait(false);
        if (preferred is null)
        {
            if (resolved is { VerifiedVersion: true, Version: not null, Kind: "Application" } &&
                WorkingFolder.Normalize(resolved.Path) is { } executable && Stamp(executable) is { } observed)
            {
                alternate = new(observed, true, resolved.Version, null);
                preferred = new(executable, Path.GetDirectoryName(executable)!);
            }
        }
        token.ThrowIfCancellationRequested();

        var processes = ReadProcesses(paths);
        await AddBinaryChecksAsync(plans, paths, native, preferred, userWinGet, machineWinGet, alternate, processes, resolved, probeFolder, token)
            .ConfigureAwait(false);

        try
        {
            await AddPathChecksAsync(plans, paths, preferred, probeFolder, token).ConfigureAwait(false);
        }
        catch (Exception error) when (IsInspectionError(error))
        {
            plans.Add(new(new(3, "InstallPathUnknown", CheckState.Unknown)));
            plans.Add(new(new(4, "InstallCommandUnknown", CheckState.Unknown)));
        }

        try
        {
            await AddDuplicateChecksAsync(plans, paths, preferred, processes, token).ConfigureAwait(false);
        }
        catch (Exception error) when (IsInspectionError(error))
        {
            plans.Add(new(new(4, "InstallDuplicatesUnknown", CheckState.Unknown)));
        }

        try
        {
            AddCleanupChecks(plans, paths, native, preferred, processes);
        }
        catch (Exception error) when (IsInspectionError(error))
        {
            plans.Add(new(new(8, "InstallCleanupUnknown", CheckState.Unknown)));
        }

        var maintenancePaths = plans.SelectMany(plan => plan.WaitingOn ?? []).Distinct(PathComparer).ToArray();
        AddProcessChecks(plans, processes, paths, maintenancePaths);
        return plans.OrderBy(plan => plan.Item.Stage).ToArray();
    }

    private sealed record Plan(CheckItem Item, Func<CancellationToken, Task<bool>>? Apply = null,
        string[]? WaitingOn = null, Func<CancellationToken, Task<DiagnosticResult>>? Install = null);
    private sealed record PreferredInstall(string Executable, string PathEntry);
    private sealed record FileStamp(string Name, long Size, long Modified, long Created, FileAttributes Attributes);

    private static Plan Mutation(int stage, string key, CheckState state, string evidence, string action,
        string target, string revision, Func<CancellationToken, Task<bool>> apply) =>
        new(new CheckItem(stage, key, state, evidence, action, target) { Revision = revision }, apply);

    private static string Fingerprint(params object?[] data) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(data)));

    private static bool IsInspectionError(Exception error) => error is IOException or UnauthorizedAccessException or
        SecurityException or ArgumentException or InvalidOperationException or Win32Exception or NotSupportedException
        or System.Text.Json.JsonException;

    private static string JoinLines(IEnumerable<string> lines) => string.Join(Environment.NewLine, lines);

    private static string WinGetLinksFor(string localAppData) => Path.Combine(localAppData, "Microsoft", "WinGet", "Links");

    private static PreferredInstall WinGetPreferred(string executable, string link)
    {
        var linked = File.Exists(link) && PathComparer.Equals(ResolvedFile(link), executable);
        return new(executable, Path.GetDirectoryName(linked ? link : executable)!);
    }

    private static string ResolvedFile(string path)
    {
        try
        {
            return File.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName ?? path;
        }
        catch (Exception error) when (IsInspectionError(error))
        {
            return path;
        }
    }

    private static bool Under(string path, string root)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative != "." && relative != ".." && !Path.IsPathRooted(relative) &&
            !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    // Every existing segment under the approved root must stay at its literal location.
    private static bool SafeUserPath(string path, string root)
    {
        if (!Path.IsPathFullyQualified(path) || !Under(path, root))
            return false;

        var parent = Path.GetFullPath(path);
        while (!PathComparer.Equals(parent, root))
        {
            try
            {
                if ((File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0)
                    return false;
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }

            if (Path.GetDirectoryName(parent) is not { } next || PathComparer.Equals(parent, next))
                return false;
            parent = next;
        }

        return (File.GetAttributes(root) & FileAttributes.ReparsePoint) == 0;
    }

    private static FileStamp? Stamp(string path)
    {
        var info = new FileInfo(path);
        return info.Exists ? new(path, info.Length, info.LastWriteTimeUtc.Ticks, info.CreationTimeUtc.Ticks, info.Attributes) : null;
    }

    internal static bool CanInstallIntoEmptyTargets()
    {
        var paths = new InstallPaths();
        try
        {
            if (!SafeUserPath(paths.Native, paths.Profile) || !SafeUserPath(paths.Versions, paths.Profile) ||
                !SafeUserPath(paths.Downloads, paths.Profile) || !SafeUserPath(paths.UserWinGet, paths.LocalAppData) ||
                !SafeUserPath(paths.UserWinGetLink, paths.LocalAppData)) return false;
            foreach (var path in new[] { paths.Native, paths.UserWinGet, paths.MachineWinGet,
                paths.UserWinGetLink, paths.MachineWinGetLink })
            {
                try { _ = File.GetAttributes(path); return false; }
                catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { }
            }
            return true;
        }
        catch (Exception error) when (IsInspectionError(error)) { return false; }
    }

    private sealed class InstallPaths
    {
        public string Profile { get; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        public string LocalAppData { get; } = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        public string Roaming { get; } = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        public string ConfigRoot { get; } = ProfileDataPaths.Config;
        public string Bin => Path.Combine(Profile, ".local", "bin");
        public string Native => Path.Combine(Bin, "claude.exe");
        public string Versions => Path.Combine(Profile, ".local", "share", "claude", "versions");
        public string Legacy => Path.Combine(ConfigRoot, "local");
        public string Downloads => Path.Combine(ConfigRoot, "downloads");
        public string UserWinGet => Diagnostics.UserWinGetClaude;
        public string MachineWinGet => Diagnostics.MachineWinGetClaude;
        public string UserWinGetLink => Path.Combine(WinGetLinksFor(LocalAppData), "claude.exe");
        public string MachineWinGetLink => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WinGet", "Links", "claude.exe");
        public string NpmPrefix => Path.Combine(Roaming, "npm");
    }
}
