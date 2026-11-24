using System.Text.Json;

namespace ClaudeLauncher;

internal static partial class InstallChecks
{
    private static string NpmPackageDirectory(string prefix) =>
        Path.Combine(prefix, "node_modules", "@anthropic-ai", "claude-code");

    private static async Task AddNpmChecksAsync(List<Plan> plans, InstallPaths paths, PreferredInstall? preferred,
        ProcessScan processes, CancellationToken token)
    {
        var candidates = new[] { paths.NpmPrefix, Environment.GetEnvironmentVariable("NPM_CONFIG_PREFIX") }
            .Concat((Environment.GetEnvironmentVariable("Path") ?? "").Split(';'))
            .Select(path => WorkingFolder.Normalize(path?.Trim().Trim('"')))
            .OfType<string>().Distinct(PathComparer);
        var installs = candidates.Where(prefix => File.Exists(Path.Combine(NpmPackageDirectory(prefix), "package.json")))
            .ToArray();
        if (installs.Length == 0)
            return;

        var system = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var nodeRoot = Path.Combine(system, "nodejs");
        var node = Path.Combine(nodeRoot, "node.exe");
        var npmCli = Path.Combine(nodeRoot, "node_modules", "npm", "bin", "npm-cli.js");
        var canCheckRunner = preferred is not null && File.Exists(node) && File.Exists(npmCli) &&
            SafeUserPath(node, system) && SafeUserPath(npmCli, system);
        var trustedRunner = canCheckRunner &&
            (await FindSignedFilesAsync([node], ["OpenJS Foundation", "Node.js Foundation"], token).ConfigureAwait(false)).Count == 1;

        foreach (var prefix in installs)
        {
            var package = NpmPackageDirectory(prefix);
            var hasReplacement = preferred is not null && !Under(ResolvedFile(preferred.Executable), package) &&
                !(PathComparer.Equals(Path.GetDirectoryName(preferred.Executable), prefix) &&
                  Path.GetFileName(preferred.Executable).Equals("claude.exe", StringComparison.OrdinalIgnoreCase));
            var target = JoinLines([Package + " (npm --global)", prefix, package]);
            var manifest = Path.Combine(package, "package.json");
            var userOwnedPrefix = Under(prefix, paths.Profile) ? paths.Profile
                : !PathComparer.Equals(prefix, paths.Roaming) && Under(prefix, paths.Roaming) ? paths.Roaming : null;
            var packageHash = userOwnedPrefix is not null && SafeUserPath(manifest, userOwnedPrefix) ? NpmManifestHash(manifest) : null;
            var eligible = hasReplacement && packageHash is not null && userOwnedPrefix is not null && trustedRunner;
            if (eligible && preferred is not null && userOwnedPrefix is not null && !FileChangeBlocked(processes, package))
            {
                var install = preferred;
                var stamp = Stamp(manifest);
                var shimFiles = ReadNpmShims(prefix);
                var approvedTarget = JoinLines(new[] { target }.Concat(shimFiles.Select(file => file.Name)));
                plans.Add(Mutation(4, "InstallNpmDuplicate", CheckState.Warning, manifest, "RemoveClaudeNpm", approvedTarget,
                    Fingerprint(stamp, packageHash, shimFiles, Stamp(node), Stamp(npmCli)), async cancellation =>
                    {
                        if (!SafeUserPath(manifest, userOwnedPrefix) || Stamp(manifest) != stamp ||
                            NpmManifestHash(manifest) != packageHash || !ReadNpmShims(prefix).SequenceEqual(shimFiles) ||
                            !(await InspectBinaryAsync(install.Executable, cancellation).ConfigureAwait(false)).Valid ||
                            FileChangeBlocked(ReadProcesses(paths), package))
                        {
                            return false;
                        }

                        var version = NpmVersion(manifest);
                        if (version is not null)
                            RepairJournal.Prepare(new RepairJournal.UndoPlan("Npm", Prefix: prefix, Version: version));
                        var temporary = Path.Combine(Path.GetTempPath(), $"blslab-npm-{Guid.NewGuid():N}");
                        var result = await RunToolAsync(node,
                            [npmCli, "uninstall", "--global", "--prefix", prefix, "--ignore-scripts", "--offline",
                             "--no-audit", "--no-fund", "--no-update-notifier", "--logs-max=0", Package],
                            cancellation, TimeSpan.FromMinutes(2), env =>
                            {
                                env.Remove("NODE_OPTIONS");
                                env.Remove("NODE_PATH");
                                // User and global npmrc settings are irrelevant to this one package removal.
                                env["NPM_CONFIG_USERCONFIG"] = temporary + ".user.npmrc";
                                env["NPM_CONFIG_GLOBALCONFIG"] = temporary + ".global.npmrc";
                                env["NPM_CONFIG_CACHE"] = temporary + ".cache";
                            }).ConfigureAwait(false);
                        return result is { ExitCode: 0 } && !File.Exists(manifest) && ReadNpmShims(prefix).Length == 0;
                    }));
            }
            else
            {
                plans.Add(new(new(4, hasReplacement ? "InstallNpmDuplicate" : "InstallNpmOnly", CheckState.Warning,
                    manifest, Target: target), WaitingOn: eligible ? [package] : null));
            }
        }
    }

    private static string? NpmManifestHash(string manifest)
    {
        try
        {
            using var file = File.Open(manifest, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (file.Length is <= 0 or > 256 * 1024)
                return null;
            using var json = JsonDocument.Parse(file);
            if (json.RootElement.ValueKind != JsonValueKind.Object || !json.RootElement.TryGetProperty("name", out var name) ||
                name.ValueKind != JsonValueKind.String || !name.ValueEquals(Package))
            {
                return null;
            }
            file.Position = 0;
            return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(file));
        }
        catch (Exception error) when (IsInspectionError(error))
        {
            return null;
        }
    }

    private static FileStamp[] ReadNpmShims(string prefix) =>
        new[] { "claude", "claude.cmd", "claude.ps1", "claude.exe" }
            .Select(name => Stamp(Path.Combine(prefix, name))).OfType<FileStamp>().ToArray();
}
