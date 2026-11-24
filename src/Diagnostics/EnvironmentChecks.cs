using System.Security;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32;

namespace ClaudeLauncher;

internal static partial class EnvironmentChecks
{
    private const string UserEnvironment = @"Environment";
    private const string MachineEnvironment = @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment";
    private const string ClearVariable = "ClearUserVariable";
    private const string SetGitBash = "SetUserGitBash";
    private static readonly string[] AuthenticationVariables =
        ["ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN", "ANTHROPIC_BASE_URL"];
    private static readonly string[] ProxyVariables = ["HTTPS_PROXY", "HTTP_PROXY", "NO_PROXY"];
    private static readonly string[] PathVariables = ["NODE_EXTRA_CA_CERTS", "CLAUDE_CODE_GIT_BASH_PATH"];
    private static readonly byte[] RevisionKey = RandomNumberGenerator.GetBytes(32);

    internal static async Task<IReadOnlyList<CheckItem>> ScanAsync(string cwd, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        List<CheckItem> variables;
        try { variables = ScanVariables(cwd); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            variables = [new(6, "InspectionError", CheckState.Unknown, ex.GetType().Name)];
        }
        var shell = ReadShellAsync(cwd, token);
        var network = ScanNetworkAsync(token);
        await Task.WhenAll(shell, network).ConfigureAwait(false);
        var git = await PrepareGitAsync(cwd, shell.Result, token).ConfigureAwait(false);
        return [PolicyItem(cwd, shell.Result), .. variables, git, .. network.Result];
    }

    internal static async Task<bool> ApplyAsync(string cwd, CheckItem approved, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (approved.Revision is null)
            return false;

        if (approved.Stage == 5 && approved.Action == "UserRemoteSigned")
            return await ApplyPolicyAsync(cwd, approved, token).ConfigureAwait(false);

        if (approved.Stage == 6 && approved.Action == InstallGitAction)
            return await ApplyGitInstallAsync(cwd, approved, token).ConfigureAwait(false);

        if (approved.Stage != 6 || approved.Action is not (ClearVariable or SetGitBash))
            return false;

        // Scope and variable are generated locally. The approval's display text is never a write input.
        var managed = ReadManagedVariables();
        foreach (var name in AuthenticationVariables.Concat(PathVariables))
        {
            token.ThrowIfCancellationRequested();
            var current = ReadVariable(name);
            var candidate = VariableItem(cwd, name, "User", current, managed.Contains(name), out _);
            if (candidate != approved)
                continue;

            // Check again immediately before writing, including the exact replacement path.
            if (VariableItem(cwd, name, "User", ReadVariable(name), ReadManagedVariables().Contains(name),
                out var replacement) != approved)
                return false;
            token.ThrowIfCancellationRequested();

            Environment.SetEnvironmentVariable(name, replacement, EnvironmentVariableTarget.User);
            if (!string.Equals(ReadVariable(name).User.Value, replacement, StringComparison.Ordinal))
                return false;

            if (Environment.GetEnvironmentVariable(name) == current.Process)
                Environment.SetEnvironmentVariable(name, replacement, EnvironmentVariableTarget.Process);
            return true;
        }
        return false;
    }

    private static List<CheckItem> ScanVariables(string cwd)
    {
        var results = new List<CheckItem>();
        var managed = ReadManagedVariables();
        foreach (var name in managed.OrderBy(name => name, StringComparer.Ordinal))
            results.Add(new(6, "EnvManaged", CheckState.Info, name, Target: name));

        foreach (var name in AuthenticationVariables.Concat(PathVariables).Concat(ProxyVariables))
        {
            var state = ReadVariable(name);
            if (state.User.Value is null && state.Machine.Value is null && state.Process is null)
            {
                if (!ProxyVariables.Contains(name))
                    results.Add(new(6, "EnvUnset", CheckState.Pass, name, Target: name));
                continue;
            }

            foreach (var scope in new[] { "User", "Machine", "Process" })
            {
                var row = VariableItem(cwd, name, scope, state, managed.Contains(name), out _);
                if (row is not null)
                    results.Add(row);
            }
        }

        if (!results.Any(item => item.Key == "ProxyConfigured"))
            results.Add(new(6, "ProxyUnset", CheckState.Info, "HTTPS_PROXY · HTTP_PROXY · NO_PROXY"));
        return results;
    }

    private static CheckItem? VariableItem(string cwd, string name, string scope, VariableState state, bool managed,
        out string? replacement)
    {
        replacement = null;
        var value = scope switch { "User" => state.User.Value, "Machine" => state.Machine.Value, _ => state.Process };
        // Show a process-only override without duplicating values already visible in saved scopes.
        if (value is null || scope == "Process" && (value == state.User.Value || value == state.Machine.Value))
            return null;

        var target = scope switch
        {
            "User" => $@"HKCU\Environment\{name}",
            "Machine" => $@"HKLM\{MachineEnvironment}\{name}",
            _ => $"Process: {name}"
        };
        var evidence = $"{name} ({scope})";
        var canChange = scope == "User" && !managed && state.Machine.Value is null &&
            (state.Process is null || state.Process == state.User.Value);

        if (ProxyVariables.Contains(name))
            return new(6, "ProxyConfigured", CheckState.Info, evidence, Target: target);

        if (AuthenticationVariables.Contains(name))
        {
            var key = name switch
            {
                "ANTHROPIC_API_KEY" => canChange ? "EnvApiKey" : "EnvApiKeyManual",
                "ANTHROPIC_AUTH_TOKEN" => canChange ? "EnvAuthToken" : "EnvAuthTokenManual",
                _ => canChange ? "EnvEndpoint" : "EnvEndpointManual"
            };
            return PrepareVariable(cwd, key, CheckState.Warning, name, evidence, target, state,
                canChange ? ClearVariable : null);
        }

        var ca = name == "NODE_EXTRA_CA_CERTS";
        var file = CheckPath(cwd, value, bash: !ca);
        if (file.Path is not null)
            evidence += $"\n{file.Path}";
        if (file.State == PathState.Exists)
            return new(6, ca ? "EnvCaFound" : "EnvGitBashFound", CheckState.Pass, evidence, Target: target);
        if (file.State == PathState.Unverified)
            return new(6, "EnvPathUnverified", CheckState.Unknown, evidence, Target: target);

        replacement = !ca && canChange ? FindStandardBash() : null;
        var action = canChange ? replacement is null ? ClearVariable : SetGitBash : null;
        var pathKey = ca ? canChange ? "EnvCaMissing" : "EnvCaMissingManual"
            : canChange ? "EnvGitBashInvalid" : "EnvGitBashInvalidManual";
        return PrepareVariable(cwd, pathKey, CheckState.Warning, name, evidence, target, state, action, replacement);
    }

    private static CheckItem PrepareVariable(string cwd, string key, CheckState status, string name,
        string evidence, string target, VariableState state, string? action, string? replacement = null)
    {
        if (action is null)
            return new(6, key, status, evidence, Target: target);

        if (state.Process is not null || replacement is not null)
            target += $"\nProcess: {name}";
        if (replacement is not null)
            target += $"\n{replacement}";
        return new(6, key, status, evidence, action, target)
        {
            Revision = Revision(cwd, key, name, action, replacement,
                state.User.Raw, state.User.Kind, state.User.Value, state.Machine.Raw,
                state.Machine.Kind, state.Machine.Value, state.Process)
        };
    }

    private static VariableState ReadVariable(string name)
    {
        using var user = Registry.CurrentUser.OpenSubKey(UserEnvironment);
        using var machine = Registry.LocalMachine.OpenSubKey(MachineEnvironment);
        return new(ReadSavedVariable(user, name), ReadSavedVariable(machine, name),
            Environment.GetEnvironmentVariable(name));
    }

    private static SavedValue ReadSavedVariable(RegistryKey? key, string name)
    {
        var value = key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (value is not string raw)
            return new(null, null, null);
        var kind = key!.GetValueKind(name);
        return new(raw, kind.ToString(), kind == RegistryValueKind.ExpandString
            ? Environment.ExpandEnvironmentVariables(raw) : raw);
    }

    private static HashSet<string> ReadManagedVariables(bool strict = false)
    {
        const string registryPath = @"SOFTWARE\Policies\ClaudeCode";
        var knownNames = AuthenticationVariables.Concat(PathVariables).Concat(ProxyVariables)
            .Concat(["DISABLE_AUTOUPDATER", "DISABLE_UPDATES"])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var machine = Registry.LocalMachine.OpenSubKey(registryPath);
        using var user = Registry.CurrentUser.OpenSubKey(registryPath);
        Add(machine?.GetValue("Settings") as string);
        Add(user?.GetValue("Settings") as string);

        var config = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ClaudeCode");
        var dropIns = Path.Combine(config, "managed-settings.d");
        AddFile(Path.Combine(config, "managed-settings.json"));
        if (strict && Directory.Exists(dropIns) && (File.GetAttributes(dropIns) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Managed configuration is redirected.");
        if (Directory.Exists(dropIns) && (File.GetAttributes(dropIns) & FileAttributes.ReparsePoint) == 0)
        {
            foreach (var file in Directory.EnumerateFiles(dropIns, "*.json"))
            {
                if (!Path.GetFileName(file).StartsWith('.') && (File.GetAttributes(file) & FileAttributes.Hidden) == 0)
                    AddFile(file);
            }
        }
        return result;

        void AddFile(string path)
        {
            var file = new FileInfo(path);
            if (strict)
            {
                try { _ = File.GetAttributes(path); }
                catch (FileNotFoundException) { return; }
                catch (DirectoryNotFoundException) { return; }
            }
            if (!file.Exists)
                return;
            if (file.Length > 1024 * 1024 || (file.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                if (strict) throw new IOException("Managed configuration is unavailable for inspection.");
                return;
            }
            try { Add(File.ReadAllText(path)); }
            catch (Exception ex) when (!strict && ex is (IOException or UnauthorizedAccessException or SecurityException)) { }
        }

        void Add(string? json)
        {
            if (json is null)
                return;
            if (json.Length > 1024 * 1024)
            {
                if (strict) throw new IOException("Managed configuration exceeds the inspection limit.");
                return;
            }
            try
            {
                using var document = JsonDocument.Parse(json);
                if (document.RootElement.ValueKind == JsonValueKind.Object &&
                    document.RootElement.TryGetProperty("env", out var env) && env.ValueKind == JsonValueKind.Object)
                {
                    foreach (var entry in env.EnumerateObject())
                    {
                        if (knownNames.TryGetValue(entry.Name, out var name))
                            result.Add(name);
                    }
                }
            }
            catch (JsonException) when (!strict) { }
        }
    }

    internal static bool HasManagedUpdateFlags()
    {
        try
        {
            var names = ReadManagedVariables(strict: true);
            return names.Contains("DISABLE_AUTOUPDATER") || names.Contains("DISABLE_UPDATES");
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException or SecurityException)
        {
            return true;
        }
    }

    private static (PathState State, string? Path) CheckPath(string cwd, string value, bool bash)
    {
        // Reject URI-like or malformed values without displaying possibly sensitive input.
        if (value.Length is 0 or > 2048 || value.Any(char.IsControl) ||
            value.Contains("://", StringComparison.Ordinal) || value.Contains('@') ||
            !Path.IsPathFullyQualified(value) && !Path.HasExtension(value) && !value.Contains('\\') && !value.Contains('/'))
            return (PathState.Unverified, null);

        string path;
        try { path = Path.GetFullPath(value, cwd); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        {
            return (PathState.Unverified, null);
        }

        if (IsNetworkPath(path))
            return (PathState.Unverified, null);

        if (bash && !new[] { "bash.exe", "sh.exe", "bash", "sh" }
            .Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase))
            return (PathState.Missing, path);
        try
        {
            var attributes = File.GetAttributes(path);
            return ((attributes & FileAttributes.Directory) == 0 ? PathState.Exists : PathState.Missing, path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return (PathState.Missing, path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            return (PathState.Unverified, path);
        }
    }

    private static bool IsNetworkPath(string path) =>
        path.StartsWith(@"\\", StringComparison.Ordinal) ||
        new DriveInfo(Path.GetPathRoot(path)!).DriveType == DriveType.Network;

    private static string? FindStandardBash() => new[]
    {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "bin", "bash.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Git", "bin", "bash.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "Git", "bin", "bash.exe")
    }.FirstOrDefault(File.Exists);

    private static string Revision(params string?[] values)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(values);
        try { return Convert.ToHexString(HMACSHA256.HashData(RevisionKey, bytes)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private enum PathState { Exists, Missing, Unverified }
    private sealed record SavedValue(string? Raw, string? Kind, string? Value);
    private sealed record VariableState(SavedValue User, SavedValue Machine, string? Process);
}
