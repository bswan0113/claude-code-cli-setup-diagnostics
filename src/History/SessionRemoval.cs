using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace ClaudeLauncher;

internal sealed record RemovedSession(Guid Id, string Title, string Cwd, DateTime RemovedUtc)
{
    internal required string ArchivePath { get; init; }
    internal required DateTime ModifiedUtc { get; init; }
}

internal sealed record SessionRemovalResult(string Code);

internal static class SessionRemoval
{
    private const string Single = ".launcher-removed-";
    private const string Group = ".launcher-group-";
    private static readonly StringComparer Paths = StringComparer.OrdinalIgnoreCase;
    private static readonly EnumerationOptions Direct = new()
    {
        IgnoreInaccessible = false, AttributesToSkip = 0, RecurseSubdirectories = false
    };

    internal static string ProjectsRoot()
    {
        if (WorkingFolder.Normalize(ProfileDataPaths.Config) is not { } root || root.StartsWith(@"\\?\") || root.StartsWith(@"\\.\"))
            throw new IOException("Invalid Claude configuration directory.");
        return Path.Combine(root, "projects");
    }

    internal static T WithMutationLock<T>(Func<T> action, TimeSpan? wait = null)
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (identity.User is not { } account) throw new IOException("User identity unavailable.");
        using var gate = new Mutex(false, @"Global\BlsLab.ClaudeLauncher.SessionRemoval." + account.Value);
        try
        {
            if (!gate.WaitOne(wait ?? TimeSpan.Zero)) throw new IOException("Another session operation is in progress.");
        }
        catch (AbandonedMutexException) { }
        try { return action(); }
        finally { gate.ReleaseMutex(); }
    }

    internal static Task<SessionRemovalResult> RemoveAsync(SavedSession session, CancellationToken token = default) =>
        Run(() =>
        {
            if (ProjectRemoval.IsPending(session.Id)) return "Conflict";
            return session.TranscriptPath is { } path
                ? RemoveLocked(session, path + Single + DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture), token)
                : "Changed";
        }, token);

    internal static Task<SessionRemovalResult> RestoreAsync(RemovedSession session, CancellationToken token = default) =>
        Run(() =>
        {
            if (!ParseSingle(session.ArchivePath, out var id, out var when) || id != session.Id || when != session.RemovedUtc)
                return "Changed";
            if (ProjectRemoval.IsPending(id)) return "Conflict";
            var original = Path.Combine(Path.GetDirectoryName(session.ArchivePath)!, id.ToString("D") + ".jsonl");
            return RestoreLocked(id, original, session.ArchivePath, session.ModifiedUtc, token);
        }, token);

    private static Task<SessionRemovalResult> Run(Func<string> operation, CancellationToken token) => LauncherProfiles.RunBound(() =>
    {
        token.ThrowIfCancellationRequested();
        try { return new SessionRemovalResult(WithMutationLock(operation)); }
        catch (Exception e) when (AccessError(e)) { return new SessionRemovalResult("Unavailable"); }
    }, token);

    internal static Task<IReadOnlyList<RemovedSession>> ListRemovedAsync(CancellationToken token = default) =>
        LauncherProfiles.RunBound<IReadOnlyList<RemovedSession>>(() =>
        {
            string root = ProjectsRoot();
            var result = new List<RemovedSession>();
            try
            {
                EnsureLiteralPath(root);
                foreach (var project in Directory.EnumerateDirectories(root, "*", Direct))
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        if ((File.GetAttributes(project) & FileAttributes.ReparsePoint) != 0) continue;
                        foreach (var entry in new DirectoryInfo(project).EnumerateFiles("*.jsonl" + Single + "*", Direct))
                        {
                            token.ThrowIfCancellationRequested();
                            var path = entry.FullName;
                            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0 || !ParseSingle(path, out var id, out var removed))
                                continue;
                            if (SessionHistory.InspectTranscript(path, id) is { } item)
                                result.Add(new(id, item.Title, item.Cwd, removed) { ArchivePath = path, ModifiedUtc = item.ModifiedUtc });
                        }
                    }
                    catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { }
                }
            }
            catch (DirectoryNotFoundException) when (!Directory.Exists(root)) { }
            return result.OrderByDescending(item => item.RemovedUtc).ThenBy(item => item.Id).ToArray();
        }, token);

    private static bool ParseSingle(string path, out Guid id, out DateTime removed)
    {
        id = default;
        removed = default;
        var name = Path.GetFileName(path);
        var marker = ".jsonl" + Single;
        if (name.Length <= 36 + marker.Length || !name.AsSpan(36, marker.Length).Equals(marker, StringComparison.OrdinalIgnoreCase) ||
            !Guid.TryParseExact(name.AsSpan(0, 36), "D", out id) || id == Guid.Empty ||
            !long.TryParse(name.AsSpan(36 + marker.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var ticks) ||
            ticks <= 0 || ticks > DateTime.MaxValue.Ticks)
            return false;
        removed = new DateTime(ticks, DateTimeKind.Utc);
        return true;
    }

    internal static string RemoveLocked(SavedSession session, string archivePath, CancellationToken token) =>
        session.TranscriptPath is { } original ? Move(session.Id, original, archivePath, session.ModifiedUtc,
            restore: false, apply: true, session.Cwd, session.Title, token) : "Changed";

    internal static string ValidateRemoveLocked(SavedSession session, string archivePath, CancellationToken token) =>
        session.TranscriptPath is { } original ? Move(session.Id, original, archivePath, session.ModifiedUtc,
            restore: false, apply: false, session.Cwd, session.Title, token) : "Changed";

    internal static string ValidateOriginalLocked(Guid id, string originalPath, string archivePath, DateTime expectedModifiedUtc,
        string expectedCwd, CancellationToken token) =>
        Move(id, originalPath, archivePath, expectedModifiedUtc, restore: false, apply: false, expectedCwd, null, token);

    internal static string RestoreLocked(Guid id, string originalPath, string archivePath,
        DateTime expectedModifiedUtc, CancellationToken token, string? expectedCwd = null) =>
        Move(id, originalPath, archivePath, expectedModifiedUtc, restore: true, apply: true, expectedCwd, null, token);

    internal static string ValidateRestoreLocked(Guid id, string originalPath, string archivePath,
        DateTime expectedModifiedUtc, CancellationToken token, string? expectedCwd = null) =>
        Move(id, originalPath, archivePath, expectedModifiedUtc, restore: true, apply: false, expectedCwd, null, token);

    private static string Move(Guid id, string original, string archive, DateTime modified,
        bool restore, bool apply, string? expectedCwd, string? expectedTitle, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            var root = ProjectsRoot();
            var project = Path.GetDirectoryName(original);
            if (project is null || !Paths.Equals(Path.GetDirectoryName(project), root) ||
                !Paths.Equals(Path.GetFullPath(original), original) || !Paths.Equals(Path.GetFullPath(archive), archive) ||
                !Paths.Equals(Path.GetDirectoryName(archive), project) || id == Guid.Empty ||
                !Paths.Equals(Path.GetFileName(original), id.ToString("D") + ".jsonl") ||
                !ValidArchive(original, archive, id))
                return "Changed";

            EnsureLiteralPath(project);
            using var directory = OpenPinned(project, directory: true);
            var source = restore ? archive : original;
            var target = restore ? original : archive;
            if (Occupied(target)) return "Conflict";
            if (HasDuplicate(root, id, restore ? null : original, token)) return "Conflict";
            if (HasLiveSession(root, id, token)) return "Active";
            using var file = OpenPinned(source, directory: false);
            if (File.GetLastWriteTimeUtc(file) != modified) return "Changed";
            var actual = SessionHistory.InspectTranscript(source, id);
            if (actual is null || actual.ModifiedUtc != modified || WorkingFolder.IsClaudeScratchpad(actual.Cwd) ||
                expectedCwd is not null && !Paths.Equals(actual.Cwd, expectedCwd) ||
                expectedTitle is not null && actual.Title != expectedTitle)
                return "Changed";
            token.ThrowIfCancellationRequested();
            if (!apply) return "Ready";
            if (HasLiveSession(root, id, token)) return "Active";
            if (HasDuplicate(root, id, restore ? null : original, token)) return "Conflict";
            token.ThrowIfCancellationRequested();
            Rename(file, Path.GetFileName(target));
            return restore ? "Restored" : "Removed";
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return "Changed"; }
        catch (Win32Exception e) when (e.NativeErrorCode is 2 or 3) { return "Changed"; }
        catch (Win32Exception e) when (e.NativeErrorCode is 80 or 183) { return "Conflict"; }
        catch (Exception e) when (AccessError(e)) { return "Unavailable"; }
    }

    private static bool ValidArchive(string original, string archive, Guid id)
    {
        if (archive.StartsWith(original + Group, StringComparison.OrdinalIgnoreCase))
            return Guid.TryParseExact(archive.AsSpan(original.Length + Group.Length), "N", out var group) && group != Guid.Empty;
        return ParseSingle(archive, out var parsed, out _) && parsed == id;
    }

    private static bool HasDuplicate(string root, Guid id, string? allowed, CancellationToken token)
    {
        var name = id.ToString("D") + ".jsonl";
        foreach (var project in Directory.EnumerateDirectories(root, "*", Direct))
        {
            token.ThrowIfCancellationRequested();
            if ((File.GetAttributes(project) & FileAttributes.ReparsePoint) != 0) continue;
            var candidate = Path.Combine(project, name);
            if (!Paths.Equals(candidate, allowed) && Occupied(candidate)) return true;
        }
        return false;
    }

    private static bool Occupied(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return false; }
    }

    private static bool HasLiveSession(string root, Guid id, CancellationToken token)
    {
        var registry = Path.Combine(Path.GetDirectoryName(root)!, "sessions");
        try { EnsureLiteralPath(registry); }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return false; }
        foreach (var path in Directory.EnumerateFiles(registry, "*.json", Direct))
        {
            token.ThrowIfCancellationRequested();
            if (!int.TryParse(Path.GetFileNameWithoutExtension(path), NumberStyles.None, CultureInfo.InvariantCulture, out var pid) || pid <= 0)
                continue;
            Process process;
            try { process = Process.GetProcessById(pid); }
            catch (ArgumentException) { continue; }
            using (process)
            {
                if (process.HasExited) continue;
                try
                {
                    EnsureLiteralPath(path);
                    using var file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    if (file.Length is <= 0 or > 64 * 1024) throw new IOException("Unreadable runtime registration.");
                    using var doc = JsonDocument.Parse(file);
                    var row = doc.RootElement;
                    if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty("sessionId", out var session) ||
                        session.ValueKind != JsonValueKind.String || !session.TryGetGuid(out var registered))
                        throw new IOException("Incomplete runtime registration.");
                    if (registered != id) continue;
                    // Ignore stale records after a PID was reused. Where identity is ambiguous, keep the session.
                    if (row.TryGetProperty("procStartFt", out var start) && start.ValueKind == JsonValueKind.String &&
                        long.TryParse(start.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var expected) &&
                        process.StartTime.ToUniversalTime().ToFileTimeUtc() != expected)
                        continue;
                    return true;
                }
                catch (FileNotFoundException) { }
            }
        }
        return false;
    }

    private static bool AccessError(Exception e) => e is IOException or InvalidDataException or UnauthorizedAccessException or Win32Exception or
        SecurityException or WaitHandleCannotBeOpenedException or ArgumentException or NotSupportedException or
        InvalidOperationException or JsonException;

    private static void EnsureLiteralPath(string path)
    {
        for (string? p = path; p is not null; p = Path.GetDirectoryName(p))
            if ((File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Redirected session storage is not eligible for a move.");
    }

    private static SafeFileHandle OpenPinned(string path, bool directory)
    {
        const uint traverse = 0x20, readAttributes = 0x80, delete = 0x10000, openReparsePoint = 0x00200000, backupSemantics = 0x02000000;
        var file = CreateFileW(path, readAttributes | (directory ? traverse : delete),
            directory ? FileShare.ReadWrite : FileShare.Read, IntPtr.Zero, 3,
            openReparsePoint | (directory ? backupSemantics : 0u), IntPtr.Zero);
        try
        {
            if (file.IsInvalid || !GetFileInformationByHandle(file, out var info))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            if (((FileAttributes)info.Attributes & FileAttributes.ReparsePoint) != 0 ||
                ((info.Attributes & (uint)FileAttributes.Directory) != 0) != directory || !directory && info.Links != 1)
                throw new IOException("Unexpected session file identity.");
            var actual = new StringBuilder(32768);
            var count = GetFinalPathNameByHandleW(file, actual, (uint)actual.Capacity, 0);
            if (count == 0 || count >= actual.Capacity) throw new IOException("Unconfirmed session path.");
            var resolved = actual.ToString();
            if (resolved.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) resolved = @"\\" + resolved[8..];
            else if (resolved.StartsWith(@"\\?\")) resolved = resolved[4..];
            if (!Paths.Equals(path, resolved)) throw new IOException("Session path changed.");
            return file;
        }
        catch { file.Dispose(); throw; }
    }

    // Move the inspected object (not a reopened path) under the pinned original directory. The
    // source handle denies write/delete sharing; ReplaceIfExists stays zero even on an external race.
    private static void Rename(SafeFileHandle source, string name)
    {
        var nameBytes = Encoding.Unicode.GetByteCount(name);
        var header = new RenameHeader { NameBytes = (uint)nameBytes };
        var offset = Marshal.OffsetOf<RenameHeader>(nameof(RenameHeader.FirstChar)).ToInt32();
        var buffer = new byte[Marshal.SizeOf<RenameHeader>() + nameBytes];
        MemoryMarshal.Write(buffer.AsSpan(), in header);
        Encoding.Unicode.GetBytes(name, buffer.AsSpan(offset));
        // A null RootDirectory and a basename rename within the source file's own directory.
        // Passing this directly to NT avoids resolving the basename against the process cwd.
        const int fileRenameInformation = 10;
        int status = NtSetInformationFile(source, out _, buffer, (uint)buffer.Length, fileRenameInformation);
        if (status < 0)
            throw new Win32Exception((int)RtlNtStatusToDosError(status));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RenameHeader { internal uint Replace; internal IntPtr Directory; internal uint NameBytes; internal ushort FirstChar; }
    [StructLayout(LayoutKind.Sequential)]
    private struct FileInfoNative
    {
        internal uint Attributes;
        internal System.Runtime.InteropServices.ComTypes.FILETIME Created, Accessed, Written;
        internal uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, FileShare share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInfoNative info);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, StringBuilder name, uint length, uint flags);
    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock { internal IntPtr Status; internal UIntPtr Information; }
    [DllImport("ntdll.dll", ExactSpelling = true)]
    private static extern int NtSetInformationFile(SafeFileHandle file, out IoStatusBlock ioStatus,
        byte[] info, uint size, int infoClass);
    [DllImport("ntdll.dll", ExactSpelling = true)]
    private static extern uint RtlNtStatusToDosError(int status);
}
