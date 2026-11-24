using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ClaudeLauncher;

public sealed record RecentFolder(string Path, DateTime LastUsedUtc);

public static class FolderHistory
{
    private const int HistoryLimit = 100;

    private static string StorePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BlsLab", "ClaudeLauncher", "recent-folders.json");

    public static Task<IReadOnlyList<RecentFolder>> LoadAsync() => Task.Run<IReadOnlyList<RecentFolder>>(() =>
    {
        var folders = ReadLocal(StorePath);
        return Sort(folders).Take(HistoryLimit).ToArray();
    });

    public static void Remember(string path)
    {
        if (WorkingFolder.Normalize(path) is not { } folder || !Directory.Exists(folder))
        {
            return;
        }

        var store = StorePath;
        var storeKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(store.ToUpperInvariant())));
        try
        {
            using var mutex = new Mutex(false, $"Local\\BlsLab.ClaudeLauncher.History.{storeKey}");
            try
            {
                if (!mutex.WaitOne(TimeSpan.FromSeconds(1)))
                {
                    return;
                }
            }
            catch (AbandonedMutexException)
            {
                // The previous writer exited. The atomic replacement leaves a complete store.
            }

            try
            {
                var folders = ReadLocal(store);
                Add(folders, folder, DateTime.UtcNow);
                WriteLocal(store, Sort(folders).Take(HistoryLimit));
            }
            finally
            {
                mutex.ReleaseMutex();
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // History is optional. A read-only profile must not block launching Claude.
        }
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

    private static Dictionary<string, RecentFolder> ReadLocal(string store)
    {
        var folders = new Dictionary<string, RecentFolder>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var file = File.Open(store, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            foreach (var entry in JsonSerializer.Deserialize<RecentFolder[]>(file) ?? [])
            {
                if (entry is not null)
                {
                    Add(folders, entry.Path, entry.LastUsedUtc);
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
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
            }

            File.Move(temporary, store, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }
}
