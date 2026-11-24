using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ClaudeLauncher;

internal static partial class ProjectRemoval
{
    private enum Phase { Removing, Archived, Restoring }

    private sealed record Member(Guid Id, string Bucket, string Cwd, DateTime ModifiedUtc)
    {
        internal string Original => Path.Combine(SessionRemoval.ProjectsRoot(), Bucket, Id.ToString("D") + ".jsonl");
        internal string Archive(Guid group) => Original + ".launcher-group-" + group.ToString("N");
    }

    private sealed record Group(Guid Id, DateTime RemovedUtc, Phase State, string[] Folders,
        Member[] Members, RecentFolder[] Previous);

    private static string Store
    {
        get
        {
            string root = SessionRemoval.ProjectsRoot().ToUpperInvariant();
            string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root)));
            return Path.Combine(ProfileDataPaths.LocalData,
                "BlsLab", "ClaudeLauncher", "removed-projects", key);
        }
    }

    private static string Manifest(Guid id) => Path.Combine(Store, id.ToString("N") + ".json");

    private static bool Exists(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Redirected transaction storage.");
            return true;
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return false; }
    }

    private static void CheckStore()
    {
        for (string? dir = Store; dir is not null; dir = Path.GetDirectoryName(dir))
            _ = Exists(dir);
    }

    private static bool Normalized(string path) => WorkingFolder.Normalize(path) is { } full &&
        Paths.Equals(path, full) && !WorkingFolder.IsClaudeScratchpad(full);

    private static bool BucketIsValid(string name) => !string.IsNullOrWhiteSpace(name) &&
        name != "." && name != ".." && name == Path.GetFileName(name) &&
        !name.EndsWith('.') && !name.EndsWith(' ') && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    private static Group ReadGroup(Guid id)
    {
        var path = Manifest(id);
        if (!Exists(path)) throw new FileNotFoundException();
        using var file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        var g = JsonSerializer.Deserialize<Group>(file);
        if (g is null || g.Id != id || id == Guid.Empty || !Enum.IsDefined(g.State) ||
            g.RemovedUtc == default || g.RemovedUtc.Kind != DateTimeKind.Utc ||
            g.Folders is null || g.Members is null || g.Previous is null ||
            (g.Folders.Length == 0 && g.Members.Length == 0) ||
            g.Folders.Any(p => p is null || !Normalized(p)) || g.Folders.Distinct(Paths).Count() != g.Folders.Length ||
            g.Members.Any(m => m is null || m.Id == Guid.Empty || !BucketIsValid(m.Bucket) || !Normalized(m.Cwd) ||
                m.ModifiedUtc == default || m.ModifiedUtc.Kind != DateTimeKind.Utc) ||
            g.Members.Select(m => m.Id).Distinct().Count() != g.Members.Length ||
            g.Previous.Any(f => f is null || !g.Folders.Contains(f.Path, Paths) || f.LastUsedUtc == default ||
                f.LastUsedUtc.Kind != DateTimeKind.Utc || f.LastUsedUtc > g.RemovedUtc))
            throw new InvalidDataException("Invalid session removal record.");
        return g;
    }

    private static IReadOnlyList<Group> ReadGroups(List<RemovedProject>? unreadable = null)
    {
        CheckStore();
        if (!Exists(Store)) return [];
        var result = new List<Group>();
        foreach (string path in Directory.EnumerateFiles(Store, "*.json", SearchOption.TopDirectoryOnly))
        {
            if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out var id))
                continue;
            try { result.Add(ReadGroup(id)); }
            catch (Exception e) when (unreadable is not null && AccessError(e))
            {
                // A bad manifest must not erase unrelated history. Keep an identifiable recovery row.
                unreadable.Add(new(id, [], [], 0, File.GetLastWriteTimeUtc(path), NeedsRecovery: true));
            }
        }
        return result;
    }

    private static void WriteAtomic<T>(string name, T value, bool replace)
    {
        CheckStore();
        Directory.CreateDirectory(Store);
        CheckStore();
        _ = Exists(name);
        var tmp = Path.Combine(Store, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var file = File.Open(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(file, value);
                file.Flush(flushToDisk: true);
            }
            File.Move(tmp, name, overwrite: replace);
        }
        finally { File.Delete(tmp); }
    }

    // Changes while a dashboard scan runs (even a complete remove-and-restore) invalidate that scan.
    private static void BeginMutation() => WriteAtomic(Path.Combine(Store, "revision"), Guid.NewGuid(), true);

    private static Guid Revision()
    {
        var path = Path.Combine(Store, "revision");
        if (!Exists(path)) return Guid.Empty;
        using var file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        return JsonSerializer.Deserialize<Guid>(file);
    }

    private static void Save(Group group, bool create = false) =>
        WriteAtomic(Manifest(group.Id), group, replace: !create);

    private static RemovedProject Summary(Group group) => new(group.Id, group.Folders,
        group.Members.Select(m => m.Cwd).Distinct(Paths).ToArray(), group.Members.Length, group.RemovedUtc,
        group.State != Phase.Archived);
}
