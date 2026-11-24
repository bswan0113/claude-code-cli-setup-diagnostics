using System.Diagnostics;

namespace ClaudeLauncher;

public partial class MainWindow
{
    private CancellationTokenSource? _usageRead;
    private bool _usageRescanQueued;
    private DateTime _nextUsagePoll;

    private void ScheduleUsageRead(bool liveOnly)
    {
        if (_usageRead is not null)
        {
            _usageRescanQueued |= !liveOnly;
            return;
        }
        _nextUsagePoll = DateTime.UtcNow.AddSeconds(12);
        if (_sessions is not { Sessions.Count: > 0 } index || _closing)
            return;

        var active = _activeIds.ToHashSet();
        var priority = active.ToHashSet();
        if (!liveOnly)
            priority.UnionWith(SessionRecency.Order(index.Sessions).Take(5).Select(session => session.Id));
        var sessions = index.Sessions.Where(session => !liveOnly || active.Contains(session.Id))
            .OrderByDescending(session => active.Contains(session.Id))
            .ThenByDescending(session => priority.Contains(session.Id)).ToArray();
        if (sessions.Length == 0)
            return;

        var source = new CancellationTokenSource();
        _usageRead = source;
        _ = ReadUsageAsync(sessions, priority, source);
    }

    private async Task ReadUsageAsync(SavedSession[] sessions, HashSet<Guid> priority, CancellationTokenSource source)
    {
        using (source)
        {
            try
            {
                // Publish the visible live/recent group before scanning older transcripts.
                foreach (var group in sessions.GroupBy(session => priority.Contains(session.Id)))
                {
                    var changes = await Task.Run(async () =>
                    {
                        var changed = new Dictionary<Guid, SavedSession>();
                        foreach (var session in group)
                        {
                            source.Token.ThrowIfCancellationRequested();
                            SessionUsage usage;
                            try
                            {
                                usage = await SessionUsageReader.ReadAsync(session, source.Token).ConfigureAwait(false);
                            }
                            catch (Exception error) when (error is not OperationCanceledException)
                            {
                                Debug.WriteLine(error.GetType().Name);
                                usage = SessionUsage.Unavailable;
                            }
                            if (usage != session.Usage)
                                changed[session.Id] = session with { Usage = usage };
                        }
                        return changed;
                    }, source.Token);
                    if (source.IsCancellationRequested || _closing || _sessions is not { } current)
                        return;
                    if (changes.Count == 0)
                        continue;

                    ApplyUsageChanges(current, changes);
                }
            }
            catch (OperationCanceledException) when (source.IsCancellationRequested) { }
            catch (Exception error)
            {
                Debug.WriteLine(error.GetType().Name);
            }
            finally
            {
                if (ReferenceEquals(_usageRead, source))
                    _usageRead = null;
                if (_usageRescanQueued && !_closing)
                {
                    _usageRescanQueued = false;
                    ScheduleUsageRead(liveOnly: false);
                }
            }
        }
    }

    private void ApplyUsageChanges(SessionIndex current, Dictionary<Guid, SavedSession> changes)
    {
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var updated = current.Sessions.Select(session =>
        {
            if (changes.TryGetValue(session.Id, out var changed) && session.ModifiedUtc == changed.ModifiedUtc &&
                string.Equals(session.TranscriptPath, changed.TranscriptPath, StringComparison.OrdinalIgnoreCase))
            {
                folders.Add(session.Cwd);
                return session with { Usage = changed.Usage };
            }
            changes.Remove(session.Id);
            return session;
        }).ToArray();
        if (folders.Count == 0)
            return;

        _sessions = current with { Sessions = updated };
        // Updating the bound labels in place preserves focus, scroll position and expanded search results.
        var byFolder = updated.Where(session => folders.Contains(session.Cwd))
            .ToLookup(session => session.Cwd, StringComparer.OrdinalIgnoreCase);
        foreach (var folder in _shownFolders.Where(folder => folders.Contains(folder.Path)))
        {
            folder.SetUsage(UsageText.Folder(byFolder[folder.Path].ToArray(),
                current.FallbackFolders.Contains(folder.Path, StringComparer.OrdinalIgnoreCase)));
            UpdateSessionLabels(folder.Children.OfType<SessionTreeRow>());
        }
        UpdateSessionLabels(RecentSessions.Items.OfType<SessionTreeRow>());
        RefreshLiveRows();

        void UpdateSessionLabels(IEnumerable<SessionTreeRow> rows)
        {
            foreach (var row in rows)
                if (changes.TryGetValue(row.Session.Id, out var session))
                    row.SetUsage(UsageText.Session(session.Usage, loading: false));
        }
    }
}
