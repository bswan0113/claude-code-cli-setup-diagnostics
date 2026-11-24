using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Security;
using System.Security.Principal;
using System.Text.Json;

namespace ClaudeLauncher;

internal sealed record LauncherProfile(string Id, string Label, string ConfigRoot);

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
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BlsLab", "ClaudeLauncher", "profiles");
    private static readonly string RegistryPath = Path.Combine(Store, "profiles.json");
    private static readonly string? ActiveId = Environment.GetEnvironmentVariable(ProfileId);
    private static readonly string? OriginalRoot = ResolveOriginalRoot();
    private static readonly Lazy<bool> HandoffAccepted = new(ConfirmHandoff);
    private static int _loginPending = Environment.GetEnvironmentVariable(LoginRequest) is { } requested &&
        requested == (ActiveId ?? DefaultId) ? 1 : 0;

    internal static bool IsAdditional => ValidId(ActiveId);
    internal static LauncherProfile Current => IsAdditional
        ? Additional(ActiveId!, Environment.GetEnvironmentVariable(ProfileLabel) ?? ActiveId!)
        : Default;
    private static LauncherProfile Default => new(DefaultId, "Default", OriginalRoot ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude"));
    private static LauncherProfile Additional(string id, string label) => new(id, label, Path.Combine(Store, id, "claude"));
    internal static string? AccountStatePath(LauncherProfile profile)
    {
        if (profile.Id == DefaultId && SamePath(profile.ConfigRoot, Default.ConfigRoot))
            return Path.Combine(OriginalRoot ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude.json");
        return ValidId(profile.Id) && SamePath(profile.ConfigRoot, Additional(profile.Id, profile.Label).ConfigRoot)
            ? Path.Combine(profile.ConfigRoot, ".claude.json") : null;
    }
    internal static bool ConsumeLoginRequest() => Interlocked.Exchange(ref _loginPending, 0) == 1;

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
                    JsonSerializer.Serialize(output, new ProfileList(1, entries));
                    output.Flush(flushToDisk: true);
                }
                File.Move(temporary, RegistryPath, overwrite: true);
            }
            catch { File.Delete(temporary); throw; }
            return profile;
        }
        finally { gate.ReleaseMutex(); }
    }

    internal static bool SwitchTo(LauncherProfile profile, bool login = false, string? cwd = null,
        string? windowPlacement = null)
    {
        try
        {
            var selected = List().FirstOrDefault(item => item.Id == profile.Id && SamePath(item.ConfigRoot, profile.ConfigRoot));
            var folder = WorkingFolder.Normalize(cwd ?? Environment.CurrentDirectory);
            var executable = Environment.ProcessPath;
            if (selected is null || folder is null || !Directory.Exists(folder) || executable is null ||
                Assembly.GetEntryAssembly() != typeof(LauncherProfiles).Assembly) return false;
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = folder };
            if (Path.GetFileName(executable).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
            {
                var assembly = Path.Combine(AppContext.BaseDirectory, typeof(LauncherProfiles).Assembly.GetName().Name + ".dll");
                if (!File.Exists(assembly)) return false;
                start.ArgumentList.Add(assembly);
            }
            var environment = start.Environment;
            environment[ProfileId] = selected.Id;
            environment[ProfileLabel] = selected.Label;
            SetOrRemove(environment, OriginalConfig, OriginalRoot);
            SetOrRemove(environment, LoginRequest, login ? selected.Id : null);
            SetOrRemove(environment, SwitchBounds, windowPlacement);
            SetOrRemove(environment, Config, selected.Id == DefaultId ? OriginalRoot : selected.ConfigRoot);
            if (selected.Id != DefaultId)
            {
                EnsureStorage(selected.ConfigRoot, create: true);
                EnsureStorage(PlatformConfig(selected), create: true);
            }
            var ticket = Guid.NewGuid().ToString("N");
            environment[Handoff] = ticket;
            using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, EventName(ticket, "ready"), out var newReady);
            using var accepted = new EventWaitHandle(false, EventResetMode.ManualReset, EventName(ticket, "accepted"), out var newAccepted);
            if (!newReady || !newAccepted) return false;
            using var child = Process.Start(start);
            if (child is null) return false;
            var timer = Stopwatch.StartNew();
            do
            {
                if (child.HasExited) return false;
                if (ready.WaitOne(100)) return accepted.Set();
            } while (timer.Elapsed < TimeSpan.FromSeconds(12));
            return false;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or Win32Exception or
            SecurityException or InvalidOperationException or ArgumentException or NotSupportedException or JsonException)
        {
            return false;
        }
    }

    // Called after the child window is created and before login starts. If the original
    // launcher timed out or closed, a late child exits instead of opening a second session UI.
    internal static bool AcceptHandoff() => HandoffAccepted.Value;

    private static bool ConfirmHandoff()
    {
        if (Environment.GetEnvironmentVariable(Handoff) is not { } ticket) return true;
        if (!ValidId(ticket)) return false;
        try
        {
            using var ready = EventWaitHandle.OpenExisting(EventName(ticket, "ready"));
            using var accepted = EventWaitHandle.OpenExisting(EventName(ticket, "accepted"));
            return ready.Set() && accepted.WaitOne(TimeSpan.FromSeconds(12));
        }
        catch (Exception e) when (e is WaitHandleCannotBeOpenedException or IOException or UnauthorizedAccessException) { return false; }
    }

    private static string EventName(string ticket, string state) => @"Local\BlsLab.ClaudeLauncher.Switch." + ticket + "." + state;

    // Launcher-to-launcher switches retain the caller's process-only authentication. Only a
    // Claude subprocess belonging to an additional claude.ai account receives this environment.
    internal static void ApplyCliEnvironment(IDictionary<string, string?> environment)
    {
        environment.Remove(LoginRequest);
        environment.Remove(Handoff);
        environment.Remove(SwitchBounds);
        if (!IsAdditional) return;
        var profile = Current;
        environment[Config] = profile.ConfigRoot;
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

    private sealed record Entry(string Id, string Label);
    private sealed record ProfileList(int Version, List<Entry> Profiles);

    private static List<Entry> ReadEntries()
    {
        try
        {
            EnsureStorage(Store, create: false);
            if ((File.GetAttributes(RegistryPath) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("The profile registry is redirected.");
            using var file = new FileStream(RegistryPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (JsonSerializer.Deserialize<ProfileList>(file) is not { Version: 1, Profiles: { } entries })
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
        var root = Environment.GetEnvironmentVariable(ValidId(ActiveId) ? OriginalConfig : Config);
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
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
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
