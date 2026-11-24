using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace ClaudeLauncher;

internal static partial class InstallChecks
{
    private static readonly EnumerationOptions RegularTree = new()
    {
        RecurseSubdirectories = true, IgnoreInaccessible = false, AttributesToSkip = FileAttributes.ReparsePoint,
    };

    private static async Task AddBinaryChecksAsync(List<Plan> plans, InstallPaths paths, BinaryState native,
        PreferredInstall? preferred, BinaryState userWinGet, BinaryState machineWinGet, BinaryState alternate, ProcessScan processes,
        string cwd, CancellationToken token)
    {
        if (native.Valid)
        {
            plans.Add(new(new(2, "InstallBinaryVerified", CheckState.Pass, BinaryEvidence(paths.Native, native))));
            return;
        }
        if (native.File is null && preferred is not null)
        {
            plans.Add(new(new(2, "InstallBinaryVerified", CheckState.Pass, BinaryEvidence(preferred.Executable, alternate))));
            return;
        }

        var absent = native.File is null && userWinGet.File is null && machineWinGet.File is null;
        var readingFailed = native.Issue == "FileReadFailed" || userWinGet.Issue == "FileReadFailed" ||
            machineWinGet.Issue == "FileReadFailed";
        var observedPath = native.File is not null || absent ? paths.Native
            : userWinGet.File is not null ? paths.UserWinGet : paths.MachineWinGet;
        var observed = native.File is not null || absent ? native : userWinGet.File is not null ? userWinGet : machineWinGet;
        plans.Add(new(new(2, readingFailed ? "InstallBinaryUnknown" : absent ? "InstallBinaryMissing" : "InstallBinaryDamaged",
            readingFailed ? CheckState.Unknown : CheckState.Failed, BinaryEvidence(observedPath, observed))));

        try
        {
            var backups = FindBackups(paths).ToArray();
            if (backups.Length != 0)
            {
                var signed = await FindSignedFilesAsync(backups.Select(file => file.Name), ["Anthropic, PBC"], token)
                    .ConfigureAwait(false);
                if (signed.Count == 0)
                {
                    plans.Add(new(new(2, "InstallBackupUnverified", CheckState.Warning,
                        JoinLines(backups.Select(file => file.Name)))));
                }
                var restoreScope = new[] { paths.Bin, paths.Versions };
                var safeDestination = signed.Count != 0 && !readingFailed && SafeUserPath(paths.Native, paths.Profile);
                var restoreBlocked = FileChangeBlocked(processes, restoreScope);
                var canRestore = safeDestination && !restoreBlocked;
                var destinationHash = canRestore && native.File is not null ? HashFile(paths.Native) : null;
                foreach (var path in signed)
                {
                    var backup = backups.First(file => PathComparer.Equals(file.Name, path));
                    var brokenAside = native.File is null ? null : NextBrokenName(paths.Native);
                    var target = JoinLines(brokenAside is null ? [backup.Name, paths.Native] : [backup.Name, paths.Native, brokenAside]);
                    if (canRestore)
                    {
                        var backupHash = HashFile(backup.Name);
                        plans.Add(Mutation(2, "InstallBackupAvailable", CheckState.Warning, path, "RestoreClaudeBackup",
                            target, Fingerprint(backup, backupHash, native.File, destinationHash, brokenAside),
                            cancellation => RestoreAsync(paths, native.File, destinationHash, backup, backupHash, brokenAside, cancellation)));
                    }
                    else
                    {
                        plans.Add(new(new(2, "InstallBackupAvailable", CheckState.Warning, path, Target: target),
                            WaitingOn: safeDestination && restoreBlocked ? restoreScope : null));
                    }
                }
            }
        }
        catch (Exception error) when (IsInspectionError(error))
        {
            plans.Add(new(new(2, "InstallBackupUnverified", CheckState.Unknown)));
        }

        if (preferred is not null)
            return;
        var targetPaths = new[] { paths.Native, paths.Versions, paths.Downloads, paths.UserWinGet, paths.UserWinGetLink };
        var installTarget = JoinLines(targetPaths);
        var command = Diagnostics.NativeInstallCommand;
        if (!readingFailed && SafeUserPath(paths.Native, paths.Profile) && !FileChangeBlocked(processes, targetPaths))
        {
            var revision = Fingerprint(native.File, Stamp(paths.UserWinGet), Stamp(Diagnostics.UserWinGetExecutable));
            var item = new CheckItem(2, "InstallNativeRecovery", CheckState.Warning, command,
                "InstallClaudeNative", installTarget) { Revision = revision };
            plans.Add(new(item, Install: cancellation => Diagnostics.InstallAsync(cwd, reinstall: true, token: cancellation)));
        }
        else
        {
            plans.Add(new(new(2, "InstallNativeRecovery", CheckState.Warning, command, Target: installTarget),
                WaitingOn: targetPaths));
        }
    }

    private static string BinaryEvidence(string path, BinaryState binary) => JoinLines(new[]
    {
        path, binary.Version is null ? null : $"Claude Code {binary.Version}", binary.Issue,
        binary.File is null ? null : SizeText(binary.File.Size),
    }.OfType<string>());

    private static string SizeText(long size) =>
        $"{size / 1048576d:F1} MiB";

    private static string HashFile(string path)
    {
        using var file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(file));
    }

    private static IEnumerable<FileStamp> FindBackups(InstallPaths paths)
    {
        var bin = ReadFiles(paths.Bin, paths.Profile, recurse: false);
        var versions = ReadFiles(paths.Versions, paths.Profile, recurse: false);
        return bin.Where(file => IsNamed(file.Name, "claude.exe.new.") || IsNamed(file.Name, "claude.exe.old."))
            .Concat(versions)
            .Where(file => file.Size >= ExpectedNativeSize)
            .OrderBy(file => IsNamed(file.Name, "claude.exe.new.") ? 0 : IsNamed(file.Name, "claude.exe.old.") ? 1 : 2)
            .ThenByDescending(file => file.Modified).ThenBy(file => file.Name, PathComparer);
    }

    private static bool IsNamed(string path, string prefix) =>
        Path.GetFileName(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

    private static string NextBrokenName(string native)
    {
        var path = $"{native}.broken.launcher";
        for (var index = 2; File.Exists(path) || Directory.Exists(path); index++)
            path = $"{native}.broken.launcher.{index.ToString(CultureInfo.InvariantCulture)}";
        return path;
    }

    private static async Task<bool> RestoreAsync(InstallPaths paths, FileStamp? destination, string? destinationHash,
        FileStamp backup, string backupHash, string? brokenAside, CancellationToken token)
    {
        if (!SafeUserPath(backup.Name, paths.Profile) || !SafeUserPath(paths.Native, paths.Profile) ||
            Stamp(backup.Name) != backup || FileChangeBlocked(ReadProcesses(paths), paths.Bin, paths.Versions))
        {
            return false;
        }

        token.ThrowIfCancellationRequested();
        Directory.CreateDirectory(paths.Bin);
        var temporary = Path.Combine(paths.Bin, $".claude-launcher-restore-{Guid.NewGuid():N}.exe");
        try
        {
            File.Copy(backup.Name, temporary, overwrite: false);
            if (!string.Equals(HashFile(temporary), backupHash, StringComparison.Ordinal) ||
                !(await InspectBinaryAsync(temporary, token).ConfigureAwait(false)).Valid)
            {
                return false;
            }

            if (Stamp(paths.Native) != destination || !SafeUserPath(paths.Native, paths.Profile) ||
                (destination is not null && !string.Equals(HashFile(paths.Native), destinationHash, StringComparison.Ordinal)) ||
                (brokenAside is not null && (!SafeUserPath(brokenAside, paths.Profile) || File.Exists(brokenAside) || Directory.Exists(brokenAside))) ||
                FileChangeBlocked(ReadProcesses(paths), paths.Bin, paths.Versions))
            {
                return false;
            }

            token.ThrowIfCancellationRequested();
            if (brokenAside is null)
                File.Move(temporary, paths.Native, overwrite: false);
            else
                File.Replace(temporary, paths.Native, brokenAside);
            var verified = await PowerShellProbe.RunAsync(Environment.SystemDirectory, executable: paths.Native)
                .ConfigureAwait(false);
            if (!verified.VerifiedVersion && SafeUserPath(paths.Native, paths.Profile) &&
                string.Equals(HashFile(paths.Native), backupHash, StringComparison.Ordinal) &&
                !FileChangeBlocked(ReadProcesses(paths), paths.Bin, paths.Versions))
            {
                if (brokenAside is null)
                {
                    File.Delete(paths.Native);
                }
                else if (SafeUserPath(brokenAside, paths.Profile) &&
                    string.Equals(HashFile(brokenAside), destinationHash, StringComparison.Ordinal))
                {
                    File.Replace(brokenAside, paths.Native, null);
                }
            }
            return verified.VerifiedVersion;
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private static FileStamp[] ReadFiles(string directory, string root, bool recurse)
    {
        if (!Directory.Exists(directory))
            return [];
        if (!SafeUserPath(directory, root))
            throw new IOException();
        var files = recurse ? Directory.EnumerateFiles(directory, "*", RegularTree) : Directory.EnumerateFiles(directory);
        return files.Select(Stamp).OfType<FileStamp>().Where(file => (file.Attributes & FileAttributes.ReparsePoint) == 0)
            .OrderBy(file => file.Name, PathComparer).ToArray();
    }

    private static void AddCleanupChecks(List<Plan> plans, InstallPaths paths, BinaryState native, PreferredInstall? preferred,
        ProcessScan processes)
    {
        var interruptedUpdate = false;
        try
        {
            var updaterArtifacts = ReadFiles(paths.Bin, paths.Profile, recurse: false).Where(file =>
                IsNamed(file.Name, "claude.exe.old.") || IsNamed(file.Name, "claude.exe.new.") ||
                IsNamed(file.Name, "claude.exe.broken.")).ToArray();
            interruptedUpdate = updaterArtifacts.Any(file => !IsNamed(file.Name, "claude.exe.old."));
            var keep = LatestOld(updaterArtifacts);
            var debris = updaterArtifacts.Where(file => file != keep).ToArray();
            if (debris.Length == 0)
            {
                plans.Add(new(new(8, "InstallDebrisClear", CheckState.Pass, keep?.Name ?? paths.Bin)));
            }
            else
            {
                AddFileCleanup(plans, debris, keep?.Name, native.Valid, processes, [paths.Bin, paths.Versions],
                    "InstallDebris", "CleanClaudeDebris", token => CleanDebrisAsync(paths, debris, keep, token));
            }
        }
        catch (Exception error) when (IsInspectionError(error))
        {
            plans.Add(new(new(8, "InstallCleanupUnknown", CheckState.Unknown, Target: paths.Bin)));
        }

        try
        {
            var downloads = ReadFiles(paths.Downloads, paths.Profile, recurse: true);
            if (downloads.Length == 0)
            {
                plans.Add(new(new(8, "InstallDownloadsClear", CheckState.Pass, paths.Downloads)));
            }
            else
            {
                string[] cacheScope = [paths.Downloads, paths.Bin, paths.Versions];
                AddFileCleanup(plans, downloads, paths.Downloads, true, processes, cacheScope, "InstallDownloads", "CleanClaudeDownloads",
                    token => Task.FromResult(DeleteFiles(paths, downloads, paths.Downloads, cacheScope, token)));
            }
        }
        catch (Exception error) when (IsInspectionError(error))
        {
            plans.Add(new(new(8, "InstallCleanupUnknown", CheckState.Unknown, Target: paths.Downloads)));
        }

        AddUpdaterCheck(plans, paths, native, preferred, interruptedUpdate);
    }

    private static void AddFileCleanup(List<Plan> plans, FileStamp[] files, string? detail, bool allowed,
        ProcessScan processes, string[] scope, string key, string action, Func<CancellationToken, Task<bool>> apply)
    {
        var target = JoinLines(files.Select(file => file.Name));
        var size = $"{files.Length.ToString(CultureInfo.InvariantCulture)} / {SizeText(files.Sum(file => file.Size))}";
        var evidence = detail is null ? size : JoinLines([size, detail]);
        if (allowed && !FileChangeBlocked(processes, scope))
            plans.Add(Mutation(8, key, CheckState.Warning, evidence, action, target, Fingerprint(files, detail), apply));
        else
            plans.Add(new(new(8, key, CheckState.Warning, evidence, Target: target), WaitingOn: allowed ? scope : null));
    }

    private static async Task<bool> CleanDebrisAsync(InstallPaths paths, FileStamp[] files, FileStamp? keep,
        CancellationToken token)
    {
        var currentKeep = LatestOld(ReadFiles(paths.Bin, paths.Profile, recurse: false));
        return keep == currentKeep && (await InspectBinaryAsync(paths.Native, token).ConfigureAwait(false)).Valid &&
            DeleteFiles(paths, files, paths.Bin, [paths.Bin, paths.Versions], token);
    }

    private static FileStamp? LatestOld(IEnumerable<FileStamp> files) =>
        files.Where(file => IsNamed(file.Name, "claude.exe.old."))
            .OrderByDescending(file => file.Modified).ThenBy(file => file.Name, PathComparer).FirstOrDefault();

    private static bool DeleteFiles(InstallPaths paths, FileStamp[] files, string root, string[] scope, CancellationToken token)
    {
        if (FileChangeBlocked(ReadProcesses(paths), scope) || Diagnostics.IsWinGetRunningInCurrentSession() ||
            files.Any(file => !Under(file.Name, root) || !SafeUserPath(file.Name, paths.Profile) || Stamp(file.Name) != file))
        {
            return false;
        }

        foreach (var file in files)
        {
            token.ThrowIfCancellationRequested();
            if (!SafeUserPath(file.Name, paths.Profile) || Stamp(file.Name) != file)
                return false;
            File.Delete(file.Name);
        }

        return files.All(file => !File.Exists(file.Name));
    }

    private static async Task AddDuplicateChecksAsync(List<Plan> plans, InstallPaths paths, PreferredInstall? preferred,
        ProcessScan processes, CancellationToken token)
    {
        try
        {
            AddLegacyCheck(plans, paths, preferred, processes);
        }
        catch (Exception error) when (IsInspectionError(error))
        {
            plans.Add(new(new(4, "InstallDuplicatesUnknown", CheckState.Unknown, Target: paths.Legacy)));
        }

        await AddNpmChecksAsync(plans, paths, preferred, processes, token).ConfigureAwait(false);
    }

    private static void AddLegacyCheck(List<Plan> plans, InstallPaths paths, PreferredInstall? preferred,
        ProcessScan processes)
    {
        if (Directory.Exists(paths.Legacy))
        {
            var files = ReadFiles(paths.Legacy, paths.Profile, recurse: true);
            var target = files.Length > 0 ? JoinLines(files.Select(file => file.Name)) : paths.Legacy;
            if (preferred is not null && !FileChangeBlocked(processes, paths.Legacy) && files.Length != 0)
            {
                plans.Add(Mutation(4, "InstallLegacy", CheckState.Warning, paths.Legacy, "CleanClaudeLegacy", target,
                    Fingerprint(files), async cancellation =>
                        (await InspectBinaryAsync(preferred.Executable, cancellation).ConfigureAwait(false)).Valid &&
                        DeleteFiles(paths, files, paths.Legacy, [paths.Legacy], cancellation)));
            }
            else
            {
                plans.Add(new(new(4, "InstallLegacy", files.Length == 0 ? CheckState.Info : CheckState.Warning,
                    paths.Legacy, Target: files.Length == 0 ? null : target),
                    WaitingOn: preferred is not null && files.Length > 0 ? [paths.Legacy] : null));
            }
        }

    }
}
