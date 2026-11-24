namespace ClaudeLauncher;

/// <summary>Orders saved sessions by the last observed launch, using transcript time for older history.</summary>
public static class SessionRecency
{
    private static readonly string Store = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BlsLab", "ClaudeLauncher", "session-launches");
    private static readonly Lazy<Dictionary<Guid, DateTime>> Launches = new(ReadHistory);
    private static readonly object Gate = new();

    public static IEnumerable<SavedSession> Order(IEnumerable<SavedSession> sessions)
    {
        lock (Gate)
        {
            return sessions.OrderByDescending(s => Launches.Value.GetValueOrDefault(s.Id, s.ModifiedUtc))
                .ThenBy(s => s.Id).ToArray();
        }
    }

    public static DateTime LastUsedUtc(SavedSession session)
    {
        lock (Gate)
        {
            return Launches.Value.GetValueOrDefault(session.Id, session.ModifiedUtc);
        }
    }

    public static void Remember(Guid id) => Remember(id, DateTime.UtcNow);

    public static void Remember(Guid id, DateTime observedUtc)
    {
        if (id == Guid.Empty || observedUtc.Kind != DateTimeKind.Utc)
            return;

        lock (Gate)
        {
            if (Launches.Value.TryGetValue(id, out var previous) && previous >= observedUtc)
                return;
            Launches.Value[id] = observedUtc;
            try
            {
                Directory.CreateDirectory(Store);
                var path = Path.Combine(Store, id.ToString("D"));
                // One empty marker per session. Independent launcher windows can update different
                // entries without replacing each other's history or storing conversation text.
                try
                {
                    using var fresh = File.Open(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                    File.SetLastWriteTimeUtc(fresh.SafeFileHandle, observedUtc);
                }
                catch (IOException) when (File.Exists(path))
                {
                    using var existing = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
                    var stored = File.GetLastWriteTimeUtc(existing.SafeFileHandle);
                    if (stored < observedUtc)
                        File.SetLastWriteTimeUtc(existing.SafeFileHandle, observedUtc);
                    else
                        Launches.Value[id] = stored;
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // Keep this run's history even when the local profile is read-only.
            }
        }
    }

    private static Dictionary<Guid, DateTime> ReadHistory()
    {
        var result = new Dictionary<Guid, DateTime>();
        try
        {
            foreach (var file in new DirectoryInfo(Store).EnumerateFiles())
                if (Guid.TryParseExact(file.Name, "D", out var id))
                    result[id] = file.LastWriteTimeUtc;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // A new profile has no observed launches yet.
        }
        return result;
    }
}
