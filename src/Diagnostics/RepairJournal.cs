using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;

namespace ClaudeLauncher;

internal sealed record RepairHistoryEntry(Guid Id, DateTimeOffset At, string Action, string Status, bool CanUndo,
    string? Subject, bool IsUndo = false);

internal static partial class RepairJournal
{
    private sealed record Entry(Guid Id, DateTimeOffset At, string Action, string Status, UndoPlan? Plan = null,
        string? Scope = null, Guid? Undoes = null);
    internal sealed record UndoPlan(string Kind, PathChange? UserPath = null, PathChange? ProcessPath = null,
        Guid Instance = default, string? Previous = null, string? Expected = null, string? PreviousProcess = null,
        string? ExpectedProcess = null, string? Prefix = null, string? Version = null, int? ValueKind = null,
        string? BackupSuffix = null);
    private sealed class Operation(Entry entry) { internal Entry Entry = entry; }
    private static readonly AsyncLocal<Operation?> Current = new();
    private static readonly Guid Instance = Guid.NewGuid();
    private static readonly string Base = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static readonly string Folder = ProfileDataPaths.LocalStore(Path.Combine(Base, "ClaudeCodeSetupDiagnostics", "repair-history"));

    internal static async Task<T> TrackAsync<T>(string action, Func<Task<T>> execute, Func<T, bool> changed,
        string? subject = null)
    {
        if (action.Length is 0 or > 80 || !action.All(char.IsAsciiLetterOrDigit))
            throw new ArgumentException("Invalid repair action.", nameof(action));
        var previous = Current.Value;
        var safeSubject = subject is "~/.claude.json" or "~/.claude/settings.json" or "ANTHROPIC_API_KEY" or
            "ANTHROPIC_AUTH_TOKEN" or "ANTHROPIC_BASE_URL" or "CLAUDE_CODE_GIT_BASH_PATH" or "NODE_EXTRA_CA_CERTS" or
            "HTTP_PROXY" or "HTTPS_PROXY" or "ALL_PROXY" or "NO_PROXY" or "DISABLE_AUTOUPDATER" ? subject : null;
        var op = new Operation(new Entry(Guid.NewGuid(), DateTimeOffset.UtcNow, action, "Pending", Scope: safeSubject));
        Save(op.Entry);
        Current.Value = op;
        try
        {
            var result = await execute().ConfigureAwait(false);
            Save(op.Entry with { Status = changed(result) ? "Applied" : "Failed" });
            return result;
        }
        catch
        {
            Save(op.Entry with { Status = "Failed" });
            throw;
        }
        finally { Current.Value = previous; }
    }

    // Persist the exact approved delta before any irreversible change, under the shared repair mutex.
    internal static void Prepare(UndoPlan plan)
    {
        if (Current.Value is not { } op)
            throw new InvalidOperationException("The approved repair is missing its history record.");
        var updated = op.Entry with { Plan = plan };
        Save(updated);
        op.Entry = updated;
    }

    internal static IReadOnlyList<RepairHistoryEntry> List() => Entries().Select(entry =>
        new RepairHistoryEntry(entry.Id, entry.At, entry.Action, entry.Status, CanUndo(entry), entry.Plan?.Version ?? entry.Scope,
            entry.Undoes.HasValue)).ToArray();

    private static bool CanUndo(Entry entry)
    {
        if (entry.Plan is not { } plan || entry.Status is not ("Applied" or "Failed" or "Pending" or "UndoFailed" or "Undoing"))
            return false;
        return entry.Action switch
        {
            "PreferClaudePath" or "RefreshClaudeProcessPath" => plan.Kind == "Path" && (plan.UserPath is not null || plan.Instance == Instance),
            "DisableClaudeUpdater" or "EnableUserClaudeUpdater" => plan.Kind == "Updater",
            "UserRemoteSigned" => plan.Kind == "Policy",
            "RemoveClaudeNpm" => plan.Kind == "Npm",
            SettingsDiagnostics.RestoreAction => plan.Kind == "Settings",
            _ => false
        };
    }

    internal static Guid? LastOwnedUpdater()
    {
        Entry[] history;
        try { history = Entries(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
        var entry = history.FirstOrDefault(e => e.Undoes is null &&
            (e.Action is "DisableClaudeUpdater" or "EnableClaudeUpdater" or "EnableUserClaudeUpdater") &&
            (e.Status is "Applied" or "Undone" or "UndoFailed"));
        return entry?.Action == "DisableClaudeUpdater" && entry.Status == "Applied" && entry.Plan?.Kind == "Updater" &&
            Environment.GetEnvironmentVariable("DISABLE_AUTOUPDATER", EnvironmentVariableTarget.User) == entry.Plan.Expected ? entry.Id : null;
    }

    private static Entry[] Entries()
    {
        GuardPath(Folder);
        IEnumerable<string> files;
        try { files = Directory.GetFiles(Folder, "*.bin"); }
        catch (DirectoryNotFoundException) { return []; }
        return files.Where(path => Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out _))
            .Select(path =>
            {
                var id = Guid.ParseExact(Path.GetFileNameWithoutExtension(path), "N");
                try { return Load(id); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or CryptographicException)
                {
                    return new Entry(id, DateTimeOffset.MinValue, "Unreadable", "Unreadable");
                }
            })
            .OrderByDescending(entry => entry.At).ThenByDescending(entry => entry.Id).ToArray();
    }

    internal static Task<bool> UndoAsync(Guid id, CancellationToken token) => Task.Run(() =>
    {
        token.ThrowIfCancellationRequested();
        using var identity = WindowsIdentity.GetCurrent();
        if (identity.User is null)
            return false;
        using var mutex = new Mutex(false, $@"Global\BlsLab.ClaudeLauncher.Repair.{identity.User.Value}");
        try { if (!mutex.WaitOne(0)) return false; }
        catch (AbandonedMutexException) { }
        try { return UndoLockedAsync(id, token).GetAwaiter().GetResult(); }
        finally { mutex.ReleaseMutex(); }
    }, token);

    internal static async Task<bool> UndoLockedAsync(Guid id, CancellationToken token)
    {
        var entry = Load(id);
        if (!CanUndo(entry) || entry.Plan is not { } plan)
            return false;
        token.ThrowIfCancellationRequested();
        var attempt = new Entry(Guid.NewGuid(), DateTimeOffset.UtcNow, entry.Action, "Undoing",
            Scope: entry.Plan?.Version ?? entry.Scope, Undoes: id);
        Save(attempt);
        Save(entry with { Status = "Undoing" });
        var undone = false;
        try
        {
            undone = plan.Kind switch
            {
                "Path" => RestorePaths(plan),
                "Updater" => RestoreUpdater(plan),
                "Policy" => await EnvironmentChecks.RestoreUserPolicyAsync(plan.Previous, token).ConfigureAwait(false),
                "Npm" => await InstallChecks.RestoreNpmAsync(plan.Prefix, plan.Version, token).ConfigureAwait(false),
                "Settings" => await SettingsDiagnostics.UndoAsync(plan, token).ConfigureAwait(false),
                _ => false
            };
            return undone;
        }
        finally
        {
            var status = undone ? "Undone" : "UndoFailed";
            Save(entry with { Status = status });
            Save(attempt with { Status = status });
        }
    }

    private static Entry Load(Guid id)
    {
        var path = Path.Combine(Folder, id.ToString("N") + ".bin");
        GuardPath(path);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (file.Length is < 1 or > 1024 * 1024)
            throw new IOException("Invalid repair history file.");
        var encrypted = new byte[(int)file.Length];
        file.ReadExactly(encrypted);
        var plain = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
        try
        {
            var entry = JsonSerializer.Deserialize<Entry>(plain);
            if (entry is null || entry.Id != id)
                throw new IOException("Invalid repair history record.");
            return entry;
        }
        catch (JsonException error) { throw new IOException("Unreadable repair history.", error); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    private static void Save(Entry entry)
    {
        var path = Path.Combine(Folder, entry.Id.ToString("N") + ".bin");
        GuardPath(path);
        Directory.CreateDirectory(Folder);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(entry);
        byte[] encrypted;
        try { encrypted = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
        var temp = Path.Combine(Folder, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(encrypted);
                stream.Flush(flushToDisk: true);
            }
            GuardPath(path);
            File.Move(temp, path, overwrite: true);
        }
        finally { File.Delete(temp); }
    }

    private static void GuardPath(string path)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Repair history path is redirected.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            if (string.Equals(current, Base, StringComparison.OrdinalIgnoreCase))
                return;
        }
        throw new IOException("Repair history path is outside the user profile.");
    }
}
