using System.Security.Principal;
using System.Text.Json;

namespace ClaudeLauncher;

internal sealed record LauncherProfile(string Id, string Label, string ConfigRoot)
{
    internal bool UsesStandardAccountState { get; init; }
}

internal static class LauncherProfiles
{
    private const string DefaultId = "default";
    private const string Config = "CLAUDE_CONFIG_DIR";
    private const string ProfileId = "BLSLAB_CLAUDE_LAUNCHER_PROFILE_ID";
    private const string ProfileLabel = "BLSLAB_CLAUDE_LAUNCHER_PROFILE_LABEL";
    private const string OriginalConfig = "BLSLAB_CLAUDE_LAUNCHER_ORIGINAL_CONFIG";
    private const string LoginRequest = "BLSLAB_CLAUDE_LAUNCHER_PROFILE_LOGIN";
    private const string Handoff = "BLSLAB_CLAUDE_LAUNCHER_PROFILE_HANDOFF";
    private const string SwitchBounds = "BLSLAB_CLAUDE_LAUNCHER_SWITCH_BOUNDS";

    private static readonly string Store = Path.Combine(
        ProfileDataPaths.LocalData, "BlsLab", "ClaudeLauncher", "profiles");
    private static readonly string RegistryPath = Path.Combine(Store, "profiles.json");
    // Older launcher versions switched accounts by starting a new process. Its saved default
    // directory must take priority over the additional user's inherited CLAUDE_CONFIG_DIR.
    private static readonly string? LegacyId = Environment.GetEnvironmentVariable(ProfileId);
    private static readonly AsyncLocal<Binding?> Pinned = new();
    private static LauncherProfile? _selected = ValidId(LegacyId)
        ? Additional(LegacyId!, Environment.GetEnvironmentVariable(ProfileLabel) ?? LegacyId!) : null;

    internal static LauncherProfile Selected => Volatile.Read(ref _selected) ?? Default;
    internal static LauncherProfile Current => Pinned.Value?.Profile ?? Selected;
    internal static bool IsAdditional => Current.Id != DefaultId;
    internal static string? CurrentConfigOverride => ConfigOverride(Current);
    private static string? ConfigOverride(LauncherProfile profile) => profile.UsesStandardAccountState ? null : profile.ConfigRoot;

    private static LauncherProfile Default
    {
        get
        {
            var configured = ResolveOriginalRoot();
            return new(DefaultId, "Default", configured ??
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude"))
                { UsesStandardAccountState = configured is null };
        }
    }

    private static LauncherProfile Additional(string id, string label) => new(id, label, Path.Combine(Store, id, "claude"));

    internal static string? AccountStatePath(LauncherProfile profile)
    {
        if (profile.Id == DefaultId && SamePath(profile.ConfigRoot, Default.ConfigRoot))
            return Path.Combine(profile.UsesStandardAccountState
                ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : profile.ConfigRoot, ".claude.json");
        return ValidId(profile.Id) && SamePath(profile.ConfigRoot, Additional(profile.Id, profile.Label).ConfigRoot)
            ? Path.Combine(profile.ConfigRoot, ".claude.json") : null;
    }

    internal static LauncherProfile Select(LauncherProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var selected = profile.Id == DefaultId ? Default : List().FirstOrDefault(item => item.Id == profile.Id);
        if (selected is null || !SamePath(selected.ConfigRoot, profile.ConfigRoot))
            throw new InvalidDataException("The selected profile is no longer available.");
        if (selected.Id != DefaultId)
        {
            EnsureStorage(selected.ConfigRoot, create: true);
            EnsureStorage(PlatformConfig(selected), create: true);
        }
        Volatile.Write(ref _selected, selected.Id == DefaultId ? null : selected);
        return selected;
    }

    internal static IDisposable Bind(LauncherProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var binding = new Binding(profile, Pinned.Value);
        Pinned.Value = binding;
        return binding;
    }

    // Task.Run captures the caller's ExecutionContext before this temporary binding is released.
    internal static Task<T> RunBound<T>(Func<T> work, CancellationToken token = default)
    {
        using var scope = Bind(Current);
        return Task.Run(work, token);
    }

    private sealed class Binding(LauncherProfile profile, Binding? previous) : IDisposable
    {
        internal LauncherProfile Profile { get; } = profile;
        public void Dispose()
        {
            if (ReferenceEquals(Pinned.Value, this)) Pinned.Value = previous;
        }
    }

    internal static IReadOnlyList<LauncherProfile> List()
    {
        var original = Default;
        return [original, .. ReadEntries().Select(entry => Additional(entry.Id, entry.Label))
            .Where(profile => !SamePath(profile.ConfigRoot, original.ConfigRoot))];
    }

    internal static LauncherProfile Create(string label)
    {
        ArgumentNullException.ThrowIfNull(label);
        label = label.Trim();
        if (!ValidLabel(label)) throw new ArgumentException("Invalid profile label.", nameof(label));
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User?.Value ?? throw new IOException("Windows user identity unavailable.");
        using var gate = new Mutex(false, @"Global\BlsLab.ClaudeLauncher.Profiles." + sid);
        try
        {
            if (!gate.WaitOne(TimeSpan.FromSeconds(3))) throw new IOException("The profile list is busy.");
        }
        catch (AbandonedMutexException) { }
        try
        {
            var entries = ReadEntries();
            if (label.Equals(Default.Label, StringComparison.OrdinalIgnoreCase) ||
                entries.Any(entry => entry.Label.Equals(label, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("A profile with this name already exists.", nameof(label));
            var id = Guid.NewGuid().ToString("N");
            var profile = Additional(id, label);
            EnsureStorage(Store, create: true);
            if (Directory.Exists(Path.Combine(Store, id)) || entries.Any(entry => entry.Id == id) ||
                SamePath(Default.ConfigRoot, profile.ConfigRoot))
                throw new IOException("Profile directory already in use.");
            entries.Add(new(id, label));
            // The registry is the commit point. Claude directories are created on first use.
            var temporary = Path.Combine(Store, id + ".tmp");
            var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            try
            {
                using (output)
                {
                    JsonSerializer.Serialize(output, new ProfileList(1, entries), DiagnosticJson.Default.ProfileList);
                    output.Flush(flushToDisk: true);
                }
                File.Move(temporary, RegistryPath, overwrite: true);
            }
            catch { File.Delete(temporary); throw; }
            return profile;
        }
        finally { gate.ReleaseMutex(); }
    }

    internal static void ApplyCliEnvironment(IDictionary<string, string?> environment)
    {
        var profile = Current;
        environment.Remove(ProfileId);
        environment.Remove(ProfileLabel);
        environment.Remove(OriginalConfig);
        environment.Remove(LoginRequest);
        environment.Remove(Handoff);
        environment.Remove(SwitchBounds);
        SetOrRemove(environment, Config, ConfigOverride(profile));
        if (profile.Id == DefaultId) return;
        foreach (var variable in AccountOverrides) environment.Remove(variable);
        // The ant CLI's default profiles live outside CLAUDE_CONFIG_DIR. A global federation
        // profile can outrank a claude.ai login even after ANTHROPIC_PROFILE has been cleared.
        environment["ANTHROPIC_CONFIG_DIR"] = PlatformConfig(profile);
    }

    private static readonly string[] AccountOverrides =
    [
        "ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN", "ANTHROPIC_BASE_URL", "ANTHROPIC_CUSTOM_HEADERS",
        "ANTHROPIC_PROFILE", "ANTHROPIC_FEDERATION_RULE_ID", "ANTHROPIC_ORGANIZATION_ID", "ANTHROPIC_WORKSPACE_ID",
        "ANTHROPIC_SERVICE_ACCOUNT_ID", "ANTHROPIC_IDENTITY_TOKEN", "ANTHROPIC_IDENTITY_TOKEN_FILE",
        "CLAUDE_CODE_OAUTH_TOKEN", "CLAUDE_CODE_OAUTH_REFRESH_TOKEN", "CLAUDE_CODE_OAUTH_SCOPES",
        "CLAUDE_CODE_OAUTH_TOKEN_FILE_DESCRIPTOR", "CLAUDE_CODE_API_KEY_FILE_DESCRIPTOR", "CLAUDE_CODE_API_KEY_HELPER",
        "CLAUDE_CODE_USE_BEDROCK", "CLAUDE_CODE_USE_VERTEX", "CLAUDE_CODE_USE_FOUNDRY",
        "CLAUDE_CODE_USE_ANTHROPIC_AWS", "CLAUDE_CODE_USE_ANTHROPIC_GOOGLE_CLOUD", "CLAUDE_CODE_USE_MANTLE",
        "CLAUDE_CODE_SKIP_BEDROCK_AUTH", "CLAUDE_CODE_SKIP_VERTEX_AUTH", "CLAUDE_CODE_SKIP_FOUNDRY_AUTH", "CLAUDE_CODE_SKIP_MANTLE_AUTH",
        "ANTHROPIC_AWS_API_KEY", "ANTHROPIC_AWS_BASE_URL", "ANTHROPIC_AWS_WORKSPACE_ID",
        "ANTHROPIC_BEDROCK_BASE_URL", "ANTHROPIC_BEDROCK_MANTLE_BASE_URL", "ANTHROPIC_VERTEX_BASE_URL",
        "ANTHROPIC_VERTEX_PROJECT_ID", "ANTHROPIC_FOUNDRY_API_KEY", "ANTHROPIC_FOUNDRY_AUTH_TOKEN",
        "ANTHROPIC_FOUNDRY_BASE_URL", "ANTHROPIC_FOUNDRY_RESOURCE", "ANTHROPIC_GOOGLE_CLOUD_PROJECT",
        "ANTHROPIC_GOOGLE_CLOUD_LOCATION", "ANTHROPIC_GOOGLE_CLOUD_WORKSPACE_ID", "ANTHROPIC_GOOGLE_CLOUD_BASE_URL"
    ];

    internal sealed record Entry(string Id, string Label);
    internal sealed record ProfileList(int Version, List<Entry> Profiles);

    private static List<Entry> ReadEntries()
    {
        try
        {
            EnsureStorage(Store, create: false);
            if ((File.GetAttributes(RegistryPath) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("The profile registry is redirected.");
            using var file = new FileStream(RegistryPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (JsonSerializer.Deserialize(file, DiagnosticJson.Default.ProfileList) is not { Version: 1, Profiles: { } entries })
                throw new InvalidDataException("Unrecognized profile registry.");
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in entries)
                if (entry is null || !ValidId(entry.Id) || !ValidLabel(entry.Label) || !ids.Add(entry.Id))
                    throw new InvalidDataException("Invalid profile registry.");
            return entries;
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return []; }
    }

    private static bool ValidId(string? id) => id is not null && Guid.TryParseExact(id, "N", out var value) &&
        value != Guid.Empty && id == value.ToString("N");
    private static bool ValidLabel(string? label) => !string.IsNullOrWhiteSpace(label) && label.Length <= 80 &&
        !label.Any(char.IsControl);
    private static bool SamePath(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    private static string PlatformConfig(LauncherProfile profile) => Path.Combine(Store, profile.Id, "anthropic");

    private static string? ResolveOriginalRoot()
    {
        var root = Environment.GetEnvironmentVariable(ValidId(LegacyId) ? OriginalConfig : Config);
        if (string.IsNullOrWhiteSpace(root)) return null;
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)); }
        catch (Exception e) when (e is ArgumentException or IOException or NotSupportedException) { return root; }
    }

    private static void SetOrRemove(IDictionary<string, string?> environment, string key, string? value)
    {
        if (value is null) environment.Remove(key);
        else environment[key] = value;
    }

    private static void EnsureStorage(string path, bool create)
    {
        var local = ProfileDataPaths.LocalData;
        var current = local;
        foreach (var name in Path.GetRelativePath(local, path).Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, name);
            if (create) Directory.CreateDirectory(current);
            var attributes = File.GetAttributes(current);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != FileAttributes.Directory)
                throw new IOException("The profile directory is redirected.");
        }
    }
}
