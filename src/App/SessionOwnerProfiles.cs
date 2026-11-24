using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace ClaudeLauncher;

internal static class SessionOwnerProfiles
{
    private const int SizeLimit = 8 * 1024 * 1024;
    private static readonly object Gate = new();
    private static readonly Dictionary<string, Cached> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static Dictionary<string, LauncherProfile> _unique = new(StringComparer.Ordinal);
    private static long _generation;
    private sealed record Cached(long Length, DateTime Created, DateTime Modified, uint Volume, ulong FileId,
        string? Account);

    internal static LauncherProfile? Find(string accountKey) => Volatile.Read(ref _unique).GetValueOrDefault(accountKey);

    internal static Task RefreshAsync(IReadOnlyList<LauncherProfile> profiles)
    {
        var generation = Interlocked.Increment(ref _generation);
        var selected = profiles.ToArray();
        return Task.Run(() =>
        {
            lock (Gate)
            {
                if (generation != Volatile.Read(ref _generation)) return;
                var matches = new Dictionary<string, LauncherProfile>(StringComparer.Ordinal);
                var ambiguous = new HashSet<string>(StringComparer.Ordinal);
                foreach (var profile in selected)
                {
                    try
                    {
                        var path = LauncherProfiles.AccountStatePath(profile) ?? throw new InvalidDataException();
                        var account = ReadAccount(path);
                        if (account is not null && !matches.TryAdd(account, profile)) ambiguous.Add(account);
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException or SecurityException or
                        InvalidDataException or JsonException or ArgumentException or NotSupportedException)
                    {
                        // An unreadable account could also match. Don't guess which tab owns this UUID.
                        matches.Clear();
                        break;
                    }
                }
                foreach (var duplicate in ambiguous) matches.Remove(duplicate);
                if (generation == Volatile.Read(ref _generation)) Volatile.Write(ref _unique, matches);
            }
        });
    }

    private static string? ReadAccount(string path)
    {
        if (Path.GetFileName(path) != ".claude.json" || !Path.IsPathFullyQualified(path)) throw new InvalidDataException();
        try
        {
            for (string? segment = path; segment is not null; segment = Path.GetDirectoryName(segment))
                if ((File.GetAttributes(segment) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException();
            using var file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            var identity = RequireLiteralFile(file.SafeFileHandle, path);
            var fileId = ((ulong)identity.IndexHigh << 32) | identity.IndexLow;
            var created = File.GetCreationTimeUtc(file.SafeFileHandle);
            var modified = File.GetLastWriteTimeUtc(file.SafeFileHandle);
            var size = file.Length;
            if (size > SizeLimit) throw new InvalidDataException();
            if (Cache.TryGetValue(path, out var cached) && (size, created, modified, identity.Volume, fileId) ==
                (cached.Length, cached.Created, cached.Modified, cached.Volume, cached.FileId)) return cached.Account;
            var bytes = new byte[checked((int)size)];
            string? key;
            try
            {
                file.ReadExactly(bytes);
                key = ParseAccount(bytes);
            }
            finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes); }
            Cache[path] = new(size, created, modified, identity.Volume, fileId, key);
            return key;
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            Cache.Remove(path);
            return null;
        }
    }

    // Only the root oauthAccount.accountUuid field describes the cached account for this tab.
    // Skip unrelated JSON values without materializing emails, user settings, or project contents.
    private static string? ParseAccount(ReadOnlySpan<byte> bytes)
    {
        var reader = new Utf8JsonReader(bytes.StartsWith("\uFEFF"u8) ? bytes[3..] : bytes);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) throw new InvalidDataException();
        bool sawAccount = false;
        string? key = null;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            bool account = reader.ValueTextEquals("oauthAccount"u8);
            if (!reader.Read()) throw new InvalidDataException();
            if (!account) { reader.Skip(); continue; }
            if (sawAccount) throw new InvalidDataException();
            sawAccount = true;
            if (reader.TokenType == JsonTokenType.Null) continue;
            if (reader.TokenType != JsonTokenType.StartObject) throw new InvalidDataException();
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                bool uuid = reader.ValueTextEquals("accountUuid"u8);
                if (!reader.Read()) throw new InvalidDataException();
                if (!uuid) { reader.Skip(); continue; }
                if (key is not null || reader.TokenType != JsonTokenType.String ||
                    !reader.TryGetGuid(out var id) || id == Guid.Empty) throw new InvalidDataException();
                key = SessionUsage.AccountKey(id);
            }
            if (key is null) throw new InvalidDataException();
        }
        if (reader.TokenType != JsonTokenType.EndObject || reader.Read()) throw new InvalidDataException();
        return key;
    }

    private static FileIdentity RequireLiteralFile(SafeFileHandle handle, string path)
    {
        var buffer = new StringBuilder(32768);
        uint length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
        if (length == 0 || length >= buffer.Capacity || !GetFileInformationByHandle(handle, out var info) ||
            info.Links != 1) throw new InvalidDataException();
        var name = buffer.ToString();
        var expected = path.StartsWith(@"\\") ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;
        if (!name.Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException();
        return info;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdentity
    {
        internal uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh;
        internal uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder path, uint capacity, uint flags);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileIdentity identity);
}
