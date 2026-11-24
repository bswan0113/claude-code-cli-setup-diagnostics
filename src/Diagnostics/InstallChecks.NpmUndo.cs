using System.Text.Json;
using System.Text.RegularExpressions;

namespace ClaudeLauncher;

internal static partial class InstallChecks
{
    private static bool IsNpmVersion(string? value) => value is { Length: > 0 and <= 100 } &&
        Regex.IsMatch(value, @"\A(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?\z",
            RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private static string? NpmVersion(string manifest) => NpmRelease(manifest)?.Version;

    private static (string Version, string Entry)? NpmRelease(string manifest)
    {
        using var file = File.OpenRead(manifest);
        if (file.Length is 0 or > 256 * 1024)
            return null;
        using var document = JsonDocument.Parse(file);
        var package = document.RootElement;
        if (!package.TryGetProperty("name", out var name) || !name.ValueEquals(Package) ||
            !package.TryGetProperty("version", out var v) || v.ValueKind != JsonValueKind.String ||
            v.GetString() is not { } version || !IsNpmVersion(version) || !package.TryGetProperty("bin", out var bin))
            return null;
        if (bin.ValueKind == JsonValueKind.Object)
        {
            if (!bin.TryGetProperty("claude", out var entry))
                return null;
            bin = entry;
        }
        if (bin.ValueKind != JsonValueKind.String || bin.GetString() is not { Length: > 0 } command ||
            Path.IsPathRooted(command))
            return null;
        var root = Path.GetDirectoryName(manifest)!;
        var path = Path.GetFullPath(command, root);
        return Under(path, root) ? (version, path) : null;
    }

    internal static async Task<bool> RestoreNpmAsync(string? prefix, string? version, CancellationToken token)
    {
        if (!IsNpmVersion(version) || string.IsNullOrWhiteSpace(prefix) ||
            !PathComparer.Equals(WorkingFolder.Normalize(prefix), prefix))
            return false;
        var paths = new InstallPaths();
        var userRoot = Under(prefix, paths.Profile) ? paths.Profile : Under(prefix, paths.Roaming) ? paths.Roaming : null;
        var package = NpmPackageDirectory(prefix);
        var manifest = Path.Combine(package, "package.json");
        if (userRoot is null || !SafeUserPath(manifest, userRoot) || FileChangeBlocked(ReadProcesses(paths), package))
            return false;

        var system = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var node = Path.Combine(system, "nodejs", "node.exe");
        var npm = Path.Combine(system, "nodejs", "node_modules", "npm", "bin", "npm-cli.js");
        if (!SafeUserPath(node, system) || !SafeUserPath(npm, system) || !File.Exists(npm) ||
            !(await FindSignedFilesAsync([node], ["OpenJS Foundation", "Node.js Foundation"], token)
                .ConfigureAwait(false)).Contains(node, PathComparer))
            return false;
        // Never replace a package, unrelated shim, or redirected path created since removal.
        if (File.Exists(manifest) || Directory.Exists(package) || File.Exists(package) || ReadNpmShims(prefix).Length != 0)
            return false;
        foreach (var name in new[] { "claude", "claude.exe", "claude.ps1", "claude.cmd" })
            if (!SafeUserPath(Path.Combine(prefix, name), userRoot) || Directory.Exists(Path.Combine(prefix, name)))
                return false;

        var emptyConfig = Path.Combine(Path.GetTempPath(), "blslab-restore-" + Guid.NewGuid().ToString("N"));
        void CleanEnvironment(IDictionary<string, string?> env)
        {
            foreach (var name in env.Keys.Where(key => key.StartsWith("NPM_CONFIG_", StringComparison.OrdinalIgnoreCase)).ToArray())
                env.Remove(name);
            env.Remove("NODE_OPTIONS");
            env.Remove("NODE_PATH");
            env["NPM_CONFIG_USERCONFIG"] = emptyConfig + ".npmrc";
            env["NPM_CONFIG_GLOBALCONFIG"] = emptyConfig + ".global.npmrc";
        }

        const string registry = "https://registry.npmjs.org/";
        var install = await RunToolAsync(node, [npm, "install", "--global", "--prefix", prefix, "--ignore-scripts",
            "--registry", registry, "--@anthropic-ai:registry=" + registry, "--no-audit", "--no-fund",
            "--no-update-notifier", "--logs-max=0", Package + "@" + version], token, TimeSpan.FromMinutes(5),
            CleanEnvironment).ConfigureAwait(false);
        if (install is not { ExitCode: 0 } || !File.Exists(manifest) || !SafeUserPath(manifest, userRoot) ||
            NpmRelease(manifest) is not { } restored || restored.Version != version ||
            !SafeUserPath(restored.Entry, package) || !File.Exists(restored.Entry) || ReadNpmShims(prefix).Length == 0)
            return false;
        if (Path.GetExtension(restored.Entry).Equals(".exe", StringComparison.OrdinalIgnoreCase))
        {
            var native = await InspectBinaryAsync(restored.Entry, token).ConfigureAwait(false);
            return native.Valid && native.Version == version;
        }
        var verified = await RunToolAsync(node, [restored.Entry, "--version"], token, TimeSpan.FromSeconds(20),
            CleanEnvironment).ConfigureAwait(false);
        return verified is { ExitCode: 0 } && verified.Output.Trim() == version + " (Claude Code)";
    }
}
