namespace ClaudeLauncher;

/// <summary>Orders saved sessions by the last observed launch, using transcript time for older history.</summary>
public static class SessionRecency
{
    private static readonly string Store = Path.Combine(
        ProfileDataPaths.LocalData,
        "BlsLab", "ClaudeLauncher", "session-launches");
    private static readonly Dictionary<string, Dictionary<Guid, DateTime>> HistoryByStore = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Gate = new();

    public static IEnumerable<SavedSession> Order(IEnumerable<SavedSession> sessions)
    {
        var store = ProfileDataPaths.LocalStore(Store);
        lock (Gate)
        {
            var history = History(store);
            return sessions.OrderByDescending(s => history.GetValueOrDefault(s.Id, s.ModifiedUtc))
                .ThenBy(s => s.Id).ToArray();
        }
    }

    public static DateTime LastUsedUtc(SavedSession session)
    {
        var store = ProfileDataPaths.LocalStore(Store);
        lock (Gate)
        {
            return History(store).GetValueOrDefault(session.Id, session.ModifiedUtc);
        }
    }

    public static void Remember(Guid id) => Remember(id, DateTime.UtcNow);

    public static void Remember(Guid id, DateTime observedUtc)
    {
        if (id == Guid.Empty || observedUtc.Kind != DateTimeKind.Utc)
            return;

        var store = ProfileDataPaths.LocalStore(Store);
        lock (Gate)
        {
            var history = History(store);
            if (history.TryGetValue(id, out var previous) && previous >= observedUtc)
                return;
            history[id] = observedUtc;
            try
            {
                Directory.CreateDirectory(store);
                var path = Path.Combine(store, id.ToString("D"));
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
                        history[id] = stored;
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // Keep this run's history even when the local profile is read-only.
            }
        }
    }

    private static Dictionary<Guid, DateTime> History(string store)
    {
        if (HistoryByStore.TryGetValue(store, out var history)) return history;
        var result = new Dictionary<Guid, DateTime>();
        try
        {
            foreach (var file in new DirectoryInfo(store).EnumerateFiles())
                if (Guid.TryParseExact(file.Name, "D", out var id))
                    result[id] = file.LastWriteTimeUtc;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // A new profile has no observed launches yet.
        }
        return HistoryByStore[store] = result;
    }
}
