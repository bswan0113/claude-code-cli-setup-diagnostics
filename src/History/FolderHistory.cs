using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ClaudeLauncher;

public sealed record RecentFolder(string Path, DateTime LastUsedUtc);

public static class FolderHistory
{
    private const int HistoryLimit = 100;

    private static string StorePath => ProfileDataPaths.LocalStore(Path.Combine(
        ProfileDataPaths.LocalData,
        "BlsLab", "ClaudeLauncher", "recent-folders.json"));

    public static Task<IReadOnlyList<RecentFolder>> LoadAsync() => LauncherProfiles.RunBound<IReadOnlyList<RecentFolder>>(() =>
    {
        var folders = ReadLocal(StorePath);
        return Sort(folders).Where(folder => !WorkingFolder.IsClaudeScratchpad(folder.Path)).Take(HistoryLimit).ToArray();
    });

    public static void Remember(string path)
    {
        if (WorkingFolder.Normalize(path) is not { } folder || WorkingFolder.IsClaudeScratchpad(folder) || !Directory.Exists(folder))
        {
            return;
        }

        var store = StorePath;
        try
        {
            WithHistoryLock(store, () =>
            {
                var folders = ReadLocal(store, strict: true);
                Add(folders, folder, DateTime.UtcNow);
                WriteLocal(store, Sort(folders).Take(HistoryLimit));
                return true;
            });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // History is optional. A read-only profile must not block launching Claude.
        }
    }

    internal static RecentFolder[] SnapshotProjects(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
            return [];
        var store = StorePath;
        return WithHistoryLock(store, () =>
        {
            var folders = ReadLocal(store, strict: true);
            return paths.Where(folders.ContainsKey).Select(path => folders[path]).ToArray();
        });
    }

    internal static void ForgetProjects(IReadOnlyList<string> paths, DateTime removedUtc)
    {
        if (paths.Count == 0)
            return;
        var store = StorePath;
        WithHistoryLock(store, () =>
        {
            var folders = ReadLocal(store, strict: true);
            if (paths.Any(path => folders.TryGetValue(path, out var row) && row.LastUsedUtc > removedUtc))
                throw new IOException("A project was opened after removal was prepared.");
            var count = folders.Count;
            foreach (var path in paths)
                folders.Remove(path);
            if (count != folders.Count)
                WriteLocal(store, Sort(folders));
            return true;
        });
    }

    internal static void RestoreProjects(IReadOnlyList<RecentFolder> previous)
    {
        if (previous.Count == 0)
            return;
        var store = StorePath;
        WithHistoryLock(store, () =>
        {
            var folders = ReadLocal(store, strict: true);
            foreach (var row in previous)
                Add(folders, row.Path, row.LastUsedUtc);
            WriteLocal(store, Sort(folders));
            return true;
        });
    }

    private static T WithHistoryLock<T>(string store, Func<T> action)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(store.ToUpperInvariant())));
        using var mutex = new Mutex(false, $"Local\\BlsLab.ClaudeLauncher.History.{key}");
        try
        {
            if (!mutex.WaitOne(TimeSpan.FromSeconds(1)))
                throw new IOException("The recent folder store is busy.");
        }
        catch (AbandonedMutexException) { }
        try { return action(); }
        finally { mutex.ReleaseMutex(); }
    }

    private static void Add(Dictionary<string, RecentFolder> folders, string? path, DateTime timestamp)
    {
        if (timestamp == default || WorkingFolder.Normalize(path) is not { } normalized)
        {
            return;
        }

        var utc = timestamp.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(timestamp, DateTimeKind.Utc)
            : timestamp.ToUniversalTime();
        if (!folders.TryGetValue(normalized, out var previous) || previous.LastUsedUtc < utc)
        {
            folders[normalized] = new RecentFolder(normalized, utc);
        }
    }

    private static IOrderedEnumerable<RecentFolder> Sort(Dictionary<string, RecentFolder> folders) =>
        folders.Values.OrderByDescending(folder => folder.LastUsedUtc)
            .ThenBy(folder => folder.Path, StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, RecentFolder> ReadLocal(string store, bool strict = false)
    {
        var folders = new Dictionary<string, RecentFolder>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var file = File.Open(store, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var entries = JsonSerializer.Deserialize<RecentFolder[]>(file);
            if (strict && entries is null)
                throw new IOException("The recent folder store is unreadable.");
            foreach (var entry in entries ?? [])
            {
                if (entry is not null)
                {
                    Add(folders, entry.Path, entry.LastUsedUtc);
                }
            }
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { }
        catch (JsonException error) when (strict) { throw new IOException("The recent folder store is unreadable.", error); }
        catch (Exception error) when (!strict && error is (IOException or UnauthorizedAccessException or JsonException))
        {
            // Continue with the Claude records when the app's store is unavailable.
        }

        return folders;
    }

    private static void WriteLocal(string store, IEnumerable<RecentFolder> folders)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(store)!);
        var temporary = $"{store}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var file = File.Open(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(file, folders);
                file.Flush(flushToDisk: true);
            }

            File.Move(temporary, store, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }
}
