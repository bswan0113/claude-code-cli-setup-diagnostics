using System.Security;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace ClaudeLauncher;

internal sealed record SettingsLocation(int Line, int Column);

internal static partial class SettingsDiagnostics
{
    internal const string RestoreAction = "RestoreSettingsBackup";
    private const int MaxBytes = 32 * 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonDocumentOptions JsonOptions = new() { MaxDepth = 512 };
    private enum Scope { Comma, Section, WholeFile }
    private sealed record Plan(CheckItem Check, byte[] Before, byte[] After, Scope Scope, string RecoveryPath);
    internal sealed record Preview(string ScopeKey, string Difference, string RecoveryPath, string? KnownSection);

    internal static Task<IReadOnlyList<CheckItem>> ScanAsync(string cwd, CancellationToken token) =>
        Task.Run<IReadOnlyList<CheckItem>>(() => Targets().Select(path => Inspect(path, token).Item).ToArray(), token);

    private static string Profile => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private static string[] Targets() =>
        [ProfileDataPaths.AccountState, Path.Combine(ProfileDataPaths.Config, "settings.json")];

    private static (CheckItem Item, Plan? Plan) Inspect(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        CheckItem Result(string key, CheckState state) => new(6, key, state, path);
        byte[] original;
        try { original = Read(path); }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return (Result("SettingsMissing", CheckState.Pass), null);
        }
        catch (Exception e) when (ReadError(e))
        {
            return (Result("SettingsUnreadable", CheckState.Unknown), null);
        }

        var error = Validate(original, out var wrongRoot);
        if (error is null)
            return (Result("SettingsValid", CheckState.Pass), null);
        var invalid = Result(wrongRoot ? "SettingsWrongRoot" : "SettingsInvalid", CheckState.Failed) with { SettingsError = error };
        try
        {
            var recovery = Change(path, original, token);
            if (recovery is null)
                return (invalid, null);
            var (scope, after, source, sourceHash) = recovery.Value;
            var fingerprint = SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new[]
                { path, scope.ToString(), Hash(original), Hash(after), source, sourceHash }, typeof(string[]), DiagnosticJson.Default));
            var revision = Convert.ToHexString(fingerprint);
            var recoveryPath = path + ".before-launcher-repair." + revision[..16];
            // After undo, an identical repair is legitimate. Keep every older original and bind
            // the next free backup name to this revision so a changed preview must be reviewed again.
            while (File.Exists(recoveryPath) || Directory.Exists(recoveryPath))
            {
                token.ThrowIfCancellationRequested();
                fingerprint = SHA256.HashData(fingerprint);
                revision = Convert.ToHexString(fingerprint);
                recoveryPath = path + ".before-launcher-repair." + revision[..16];
            }
            var target = string.IsNullOrEmpty(source) ? path : string.Join(Environment.NewLine, path, source);
            var proposed = invalid with { Action = RestoreAction, Revision = revision, Target = target };
            return (proposed, new(proposed, original, after, scope, recoveryPath));
        }
        catch (Exception e) when (ReadError(e))
        {
            return (invalid, null);
        }
    }

    private static bool ReadError(Exception err) =>
        err is IOException or UnauthorizedAccessException or SecurityException or NotSupportedException or ArgumentException;

    private static void CheckPath(string path)
    {
        if (!Path.IsPathFullyQualified(path))
            throw new IOException("Invalid Claude configuration directory.");
        var home = Path.GetFullPath(Profile);
        var current = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(home, current);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar))
            throw new IOException("Unrecognized settings path.");
        // Ascend from the trusted profile first: a missing file through a junction must not be writable.
        var cursor = home;
        if ((File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Profile is redirected.");
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar))
        {
            cursor = Path.Combine(cursor, segment);
            if ((File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Settings path is redirected.");
        }
    }

    private static byte[] Read(string path)
    {
        CheckPath(path);
        using var file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (file.Length > MaxBytes)
            throw new IOException("Settings size exceeds the inspection limit.");
        var bytes = new byte[checked((int)file.Length)];
        file.ReadExactly(bytes);
        return bytes;
    }

    private static ReadOnlyMemory<byte> Content(byte[] bytes) =>
        bytes.AsSpan().StartsWith("\uFEFF"u8) ? bytes.AsMemory(3) : bytes.AsMemory();
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static SettingsLocation? Validate(byte[] bytes, out bool wrongRoot)
    {
        wrongRoot = false;
        var content = Content(bytes);
        try
        {
            _ = StrictUtf8.GetCharCount(content.Span);
            using var doc = JsonDocument.Parse(content, JsonOptions);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
                return null;
            wrongRoot = true;
            var offset = 0;
            while (offset < content.Length && White(content.Span[offset]))
                offset++;
            return Location(content.Span, offset);
        }
        catch (DecoderFallbackException error)
        {
            return Location(content.Span, Math.Clamp(error.Index, 0, content.Length));
        }
        catch (JsonException error)
        {
            var line = (int)(error.LineNumber ?? 0);
            var start = 0;
            for (var i = 0; i < line; i++)
            {
                int next = content.Span[start..].IndexOf((byte)'\n');
                if (next < 0) break;
                start += next + 1;
            }
            var columnByte = Math.Clamp((int)(error.BytePositionInLine ?? 0), 0, content.Length - start);
            return Location(content.Span, start + columnByte);
        }
    }

    private static SettingsLocation Location(ReadOnlySpan<byte> bytes, int offset)
    {
        var prefix = bytes[..offset];
        var lastNewline = prefix.LastIndexOf((byte)'\n');
        var column = 1;
        foreach (var value in prefix[(lastNewline + 1)..])
            if ((value & 0xC0) != 0x80) column++; // Count Unicode scalars, not UTF-8 bytes.
        return new(1 + prefix.Count((byte)'\n'), column);
    }

    private static Plan? Recheck(CheckItem approved, CancellationToken token)
    {
        if (approved.Action != RestoreAction || approved.Revision is null || approved.Stage != 6)
            return null;
        var path = Targets().SingleOrDefault(candidate => string.Equals(candidate, approved.Evidence, StringComparison.OrdinalIgnoreCase));
        if (path is null)
            return null;
        var plan = Inspect(path, token).Plan;
        return plan?.Check == approved ? plan : null;
    }

    internal static Task<Preview?> PreviewAsync(CheckItem approved, CancellationToken token) =>
        Task.Run(() =>
        {
            if (Recheck(approved, token) is not { } plan) return null;
            var section = plan.Scope == Scope.Section ? BrokenSection(plan.Before)?.Key : null;
            // User-defined keys (including project paths, server names and identifiers) stay redacted.
            if (section is not ("permissions" or "env" or "hooks" or "projects" or "mcpServers" or
                "oauthAccount" or "customApiKeyResponses" or "enabledPlugins" or "statusLine" or "sandbox"))
                section = null;
            return new Preview("SettingsScope" + plan.Scope, RedactedDiff(plan.Before, plan.After), plan.RecoveryPath, section);
        }, token);

    internal static Task<bool> ApplyAsync(string cwd, CheckItem approved, CancellationToken token) => Task.Run(() =>
    {
        var plan = Recheck(approved, token);
        if (plan is null || Validate(plan.After, out _) is not null)
            return false;
        var path = plan.Check.Evidence;
        var temp = Path.Combine(Path.GetDirectoryName(path)!, ".claude-launcher-" + Guid.NewGuid().ToString("N") + ".tmp");
        var created = false;
        try
        {
            token.ThrowIfCancellationRequested();
            using (var staged = CreatePrivate(temp))
            {
                created = true;
                staged.Write(plan.After);
                staged.Flush(true);
            }
            token.ThrowIfCancellationRequested();
            if (Recheck(approved, token) is null || !Read(temp).AsSpan().SequenceEqual(plan.After))
                return false;
            RepairJournal.Prepare(new RepairJournal.UndoPlan("Settings", Prefix: Path.GetFileName(path),
                Previous: Hash(plan.Before), Expected: Hash(plan.After), BackupSuffix: approved.Revision![..16]));
            File.Replace(temp, path, plan.RecoveryPath, ignoreMetadataErrors: false);
            created = false;
            // If the target was replaced between validation and rename, preserve that newer original.
            if (!Read(plan.RecoveryPath).AsSpan().SequenceEqual(plan.Before))
            {
                if (Read(path).AsSpan().SequenceEqual(plan.After))
                    RollBackSwap(plan.RecoveryPath, path, Hash(plan.After));
                return false;
            }
            return Read(path).AsSpan().SequenceEqual(plan.After);
        }
        finally { if (created) File.Delete(temp); }
    }, token);

    private static FileStream CreatePrivate(string path)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var owner = identity.User ?? throw new SecurityException("Account identity unavailable.");
        var security = new FileSecurity();
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new FileSystemAccessRule(owner, FileSystemRights.FullControl, AccessControlType.Allow));
        return new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.FullControl, FileShare.None,
            4096, FileOptions.WriteThrough, security);
    }

    private static void RollBackSwap(string displaced, string path, string expectedCurrent)
    {
        if (Hash(Read(path)) != expectedCurrent) return;
        // Keep any intervening third-party edit in a separate file even if it arrives during rollback.
        var rescued = path + ".launcher-conflict." + Guid.NewGuid().ToString("N");
        File.Replace(displaced, path, rescued, ignoreMetadataErrors: false);
        if (Hash(Read(rescued)) == expectedCurrent)
            File.Delete(rescued);
    }

    internal static Task<bool> UndoAsync(RepairJournal.UndoPlan plan, CancellationToken token) => Task.Run(() =>
    {
        if (plan.Kind != "Settings" || plan.BackupSuffix is not { Length: 16 } suffix || !suffix.All(char.IsAsciiHexDigit) ||
            plan.Previous is not { Length: 64 } previous || plan.Expected is not { Length: 64 } expected ||
            !previous.All(char.IsAsciiHexDigit) || !expected.All(char.IsAsciiHexDigit) || previous == expected)
            return false;
        var path = Targets().SingleOrDefault(target => Path.GetFileName(target) == plan.Prefix);
        if (path is null) return false;
        // Prefix is an enum-like basename, never a path supplied by a journal entry.
        var originalBackup = path + ".before-launcher-repair." + suffix;
        var undoBackup = path + ".before-launcher-undo." + suffix;
        var temp = Path.Combine(Path.GetDirectoryName(path)!, ".claude-launcher-" + Guid.NewGuid().ToString("N") + ".tmp");
        var created = false;
        try
        {
            token.ThrowIfCancellationRequested();
            var original = Read(originalBackup);
            if (Hash(original) != previous) return false;
            var current = Hash(Read(path));
            if (File.Exists(undoBackup))
                return current == previous && Hash(Read(undoBackup)) == expected;
            if (current != expected || Directory.Exists(undoBackup)) return false;
            using (var staged = CreatePrivate(temp))
            {
                created = true;
                staged.Write(original);
                staged.Flush(true);
            }
            token.ThrowIfCancellationRequested();
            if (Hash(Read(path)) != expected || Hash(Read(temp)) != previous ||
                Hash(Read(originalBackup)) != previous || File.Exists(undoBackup) || Directory.Exists(undoBackup))
                return false;
            File.Replace(temp, path, undoBackup, ignoreMetadataErrors: false);
            created = false;
            if (Hash(Read(undoBackup)) != expected)
            {
                RollBackSwap(undoBackup, path, previous);
                return false;
            }
            return Hash(Read(path)) == previous;
        }
        catch (Exception error) when (ReadError(error)) { return false; }
        finally { if (created) File.Delete(temp); }
    }, token);
}
