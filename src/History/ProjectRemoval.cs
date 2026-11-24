using System.ComponentModel;
using System.Security;
using System.Text.Json;

namespace ClaudeLauncher;

internal sealed record ProjectRemovalPreview(IReadOnlyList<string> Folders, IReadOnlyList<SavedSession> Sessions, string Code)
{
    internal IReadOnlyList<RecentFolder> Previous { get; init; } = [];
}

internal sealed record RemovedProject(Guid Id, IReadOnlyList<string> Folders, IReadOnlyList<string> SessionFolders,
    int SessionCount, DateTime RemovedUtc, bool NeedsRecovery);

internal static partial class ProjectRemoval
{
    private static readonly StringComparer Paths = StringComparer.OrdinalIgnoreCase;
    private static bool AccessError(Exception error) => error is IOException or InvalidDataException or
        UnauthorizedAccessException or SecurityException or JsonException or Win32Exception or
        ArgumentException or NotSupportedException or WaitHandleCannotBeOpenedException;

    internal static Task<ProjectRemovalPreview> PreviewBatchAsync(IReadOnlyList<string> folders,
        IReadOnlyList<SavedSession> sessions, CancellationToken token = default) => Task.Run(() =>
    {
        try { return Preview(folders, sessions, token); }
        catch (Exception e) when (AccessError(e)) { return new ProjectRemovalPreview([], [], "Unavailable"); }
    }, token);

    private static ProjectRemovalPreview Preview(IReadOnlyList<string> folders, IReadOnlyList<SavedSession> requested,
        CancellationToken token)
    {
        var selected = folders.Select(WorkingFolder.Normalize).ToArray();
        if (selected.Any(f => f is null || WorkingFolder.IsClaudeScratchpad(f)) ||
            (selected.Length == 0 && requested.Count == 0))
            return new([], [], "Changed");
        string[] projects = selected.OfType<string>().Distinct(Paths).Order(Paths).ToArray();
        var index = SessionHistory.LoadAsync().GetAwaiter().GetResult();
        token.ThrowIfCancellationRequested();
        if (projects.Any(LiveTerminal.HasActiveProject)) return new(projects, [], "Active");
        if (index.FallbackFolders.Any(f => projects.Contains(f, Paths))) return new(projects, [], "Unavailable");

        var members = index.Sessions.Where(s => projects.Contains(s.Cwd, Paths)).ToDictionary(s => s.Id);
        var byId = index.Sessions.ToDictionary(s => s.Id);
        foreach (var item in requested)
        {
            if (!byId.TryGetValue(item.Id, out var saved) || !SameSession(saved, item))
                return new(projects, [], "Changed");
            members[saved.Id] = saved;
        }
        var previous = projects.Length == 0 ? [] : FolderHistory.SnapshotProjects(projects);
        if (projects.Any(folder => !members.Values.Any(s => Paths.Equals(s.Cwd, folder)) &&
            !previous.Any(f => Paths.Equals(f.Path, folder))))
            return new(projects, [], "Changed");
        var sessions = members.Values.OrderBy(s => s.Id).ToArray();
        var candidate = Guid.NewGuid();
        foreach (var item in sessions)
        {
            var code = SessionRemoval.ValidateRemoveLocked(item, Archive(item.TranscriptPath!, candidate), token);
            if (code != "Ready") return new(projects, sessions, code);
        }
        return new(projects, sessions, "Ready") { Previous = previous };
    }

    private static bool SameSession(SavedSession a, SavedSession b) => a.Id == b.Id &&
        a.ModifiedUtc == b.ModifiedUtc && Paths.Equals(a.Cwd, b.Cwd) && a.Title == b.Title &&
        a.TranscriptPath is not null && Paths.Equals(a.TranscriptPath, b.TranscriptPath);

    private static string Archive(string original, Guid group) => original + ".launcher-group-" + group.ToString("N");

    private static bool SameFolders(IReadOnlyList<RecentFolder> left, IReadOnlyList<RecentFolder> right) =>
        left.Count == right.Count && left.All(a => right.Any(b => Paths.Equals(a.Path, b.Path) &&
            a.LastUsedUtc == b.LastUsedUtc));

    internal static Task<SessionRemovalResult> RemoveAsync(ProjectRemovalPreview preview, CancellationToken token = default) =>
        Run(() => Remove(preview, token), token);

    private static string Remove(ProjectRemovalPreview preview, CancellationToken token)
    {
        if (preview.Code != "Ready") return preview.Code;
        var fresh = Preview(preview.Folders, preview.Sessions, token);
        if (fresh.Code != "Ready") return fresh.Code;
        if (fresh.Sessions.Count != preview.Sessions.Count || !SameFolders(fresh.Previous, preview.Previous)) return "Changed";
        var ids = fresh.Sessions.Select(s => s.Id).ToHashSet();
        if (ReadGroups().Any(g => g.State != Phase.Archived && (g.Members.Any(m => ids.Contains(m.Id) ||
            fresh.Folders.Contains(m.Cwd, Paths)) || g.Folders.Any(f => fresh.Folders.Contains(f, Paths) ||
            fresh.Sessions.Any(s => Paths.Equals(s.Cwd, f)))))) return "Conflict";

        var members = fresh.Sessions.Select(s => new Member(s.Id, Path.GetFileName(Path.GetDirectoryName(s.TranscriptPath!)!)!,
            s.Cwd, s.ModifiedUtc)).ToArray();
        var group = new Group(Guid.NewGuid(), DateTime.UtcNow, Phase.Removing, fresh.Folders.ToArray(),
            members, fresh.Previous.ToArray());
        if (group.Previous.Any(f => f.LastUsedUtc > group.RemovedUtc)) return "Changed";
        BeginMutation();
        Save(group, create: true); // Every original location is durable before the first rename.
        int moved = 0;
        bool forgetAttempted = false;
        try
        {
            for (int i = 0; i < members.Length; i++)
            {
                var code = SessionRemoval.RemoveLocked(fresh.Sessions[i], members[i].Archive(group.Id), token);
                if (code != "Removed") return UndoFailedRemoval(group, moved, forgetAttempted, code);
                moved++;
            }
            if (group.Folders.Length != 0)
            {
                // Claude may create a session outside the launcher's mutation lock after preflight.
                var index = SessionHistory.LoadAsync().GetAwaiter().GetResult();
                if (group.Folders.Any(LiveTerminal.HasActiveProject)) return UndoFailedRemoval(group, moved, false, "Active");
                if (index.Sessions.Any(s => group.Folders.Contains(s.Cwd, Paths)) ||
                    index.FallbackFolders.Any(f => group.Folders.Contains(f, Paths)))
                    return UndoFailedRemoval(group, moved, false, "Changed");
                forgetAttempted = true;
                FolderHistory.ForgetProjects(group.Folders, group.RemovedUtc);
            }
            Save(group with { State = Phase.Archived });
            return "Removed";
        }
        catch (OperationCanceledException) { UndoFailedRemoval(group, moved, forgetAttempted, "Changed"); throw; }
        catch (Exception e) when (AccessError(e)) { return UndoFailedRemoval(group, moved, forgetAttempted, "Unavailable"); }
    }

    private static string UndoFailedRemoval(Group group, int moved, bool forgetAttempted, string code)
    {
        // An outside change to an untouched member must not interfere with rolling back the moves we made.
        try
        {
            bool restored = true;
            for (int i = moved - 1; i >= 0; i--)
            {
                var m = group.Members[i];
                if (SessionRemoval.RestoreLocked(m.Id, m.Original, m.Archive(group.Id), m.ModifiedUtc,
                    CancellationToken.None, m.Cwd) != "Restored")
                    restored = false;
            }
            if (forgetAttempted && group.Previous.Length != 0) FolderHistory.RestoreProjects(group.Previous);
            if (!restored) return "Unavailable"; // Trash retains the incomplete group.
            File.Delete(Manifest(group.Id));
            return code;
        }
        catch (Exception e) when (AccessError(e)) { return "Unavailable"; }
    }

    internal static Task<SessionRemovalResult> RestoreAsync(RemovedProject removed, CancellationToken token = default) =>
        Run(() =>
        {
            CheckStore();
            if (!Exists(Manifest(removed.Id))) return "Changed";
            var group = ReadGroup(removed.Id);
            if (removed.RemovedUtc != group.RemovedUtc || removed.SessionCount != group.Members.Length) return "Changed";
            var code = CheckRestore(group, token);
            if (code != "Ready") return code;
            BeginMutation();
            Save(group with { State = Phase.Restoring });
            return RestoreMembers(group, token);
        }, token);

    private static string CheckRestore(Group group, CancellationToken token)
    {
        if (group.Folders.Any(LiveTerminal.HasActiveProject)) return "Active";
        foreach (var item in group.Members)
        {
            token.ThrowIfCancellationRequested();
            if (Exists(item.Archive(group.Id)))
            {
                var code = SessionRemoval.ValidateRestoreLocked(item.Id, item.Original, item.Archive(group.Id),
                    item.ModifiedUtc, token, item.Cwd);
                if (code != "Ready") return code;
            }
            else
            {
                if (group.State == Phase.Archived) return "Changed";
                var code = SessionRemoval.ValidateOriginalLocked(item.Id, item.Original, item.Archive(group.Id),
                    item.ModifiedUtc, item.Cwd, token);
                if (code != "Ready") return code;
            }
        }
        return "Ready";
    }

    private static string RestoreMembers(Group group, CancellationToken token)
    {
        foreach (var item in group.Members)
        {
            token.ThrowIfCancellationRequested();
            if (!Exists(item.Archive(group.Id)))
            {
                var ready = SessionRemoval.ValidateOriginalLocked(item.Id, item.Original, item.Archive(group.Id),
                    item.ModifiedUtc, item.Cwd, token);
                if (ready != "Ready") return ready;
                continue;
            }
            var code = SessionRemoval.RestoreLocked(item.Id, item.Original, item.Archive(group.Id), item.ModifiedUtc,
                token, item.Cwd);
            if (code != "Restored") return code;
        }
        if (group.Previous.Length != 0) FolderHistory.RestoreProjects(group.Previous);
        File.Delete(Manifest(group.Id));
        return "Restored";
    }

    private static Task<SessionRemovalResult> Run(Func<string> action, CancellationToken token) => Task.Run(() =>
    {
        token.ThrowIfCancellationRequested();
        try { return new SessionRemovalResult(SessionRemoval.WithMutationLock(action)); }
        catch (Exception e) when (AccessError(e)) { return new SessionRemovalResult("Unavailable"); }
    }, token);

    // Called under the shared mutex by the independent single-session operations.
    internal static bool IsPending(Guid id) =>
        ReadGroups().Any(g => g.State != Phase.Archived && g.Members.Any(m => m.Id == id));

    internal static Task<IReadOnlyList<RemovedProject>> ListRemovedAsync() => Task.Run<IReadOnlyList<RemovedProject>>(() =>
        SessionRemoval.WithMutationLock(() =>
        {
            var unreadable = new List<RemovedProject>();
            return ReadGroups(unreadable).Select(Summary).Concat(unreadable).OrderByDescending(g => g.RemovedUtc).ToArray();
        }, TimeSpan.FromMinutes(1)));

    // Keep expensive JSONL reads outside the mutation mutex. If a group changed meanwhile, rescan.
    internal static async Task<(SessionIndex Index, IReadOnlyList<RecentFolder> Folders)> LoadVisibleAsync()
    {
        while (true)
        {
            Guid? before = await Task.Run(() =>
            {
                try { return (Guid?)SessionRemoval.WithMutationLock(Revision, TimeSpan.FromMinutes(1)); }
                catch (Exception e) when (AccessError(e)) { return null; }
            }).ConfigureAwait(false);
            var sessions = SessionHistory.LoadAsync();
            var folders = FolderHistory.LoadAsync();
            await Task.WhenAll(sessions, folders).ConfigureAwait(false);
            var index = await sessions.ConfigureAwait(false);
            var recent = await folders.ConfigureAwait(false);
            if (before is null) return (index with { HasReadErrors = true }, recent);
            (Guid Stamp, Group[] Pending, bool HasErrors) snapshot;
            try
            {
                snapshot = await Task.Run(() => SessionRemoval.WithMutationLock(() =>
                {
                    var unreadable = new List<RemovedProject>();
                    var groups = ReadGroups(unreadable).Where(g => g.State != Phase.Archived).ToArray();
                    return (Revision(), groups, unreadable.Count != 0);
                }, TimeSpan.FromMinutes(1))).ConfigureAwait(false);
            }
            catch (Exception e) when (AccessError(e)) { return (index with { HasReadErrors = true }, recent); }

            if (snapshot.Stamp != before) continue;
            if (snapshot.Pending.Length == 0)
                return (index with { HasReadErrors = index.HasReadErrors || snapshot.HasErrors }, recent);
            var pending = snapshot.Pending.SelectMany(g => g.Members).ToLookup(m => m.Id);
            var hiddenFolders = snapshot.Pending.SelectMany(g => g.Folders.Select(f => (Folder: f, g.RemovedUtc)))
                .ToLookup(pair => pair.Folder, Paths);
            var visible = index.Sessions.Where(s => !pending[s.Id].Any(m =>
                m.ModifiedUtc == s.ModifiedUtc && Paths.Equals(m.Original, s.TranscriptPath))).ToArray();
            var visibleFolders = recent.Where(f => !hiddenFolders[f.Path].Any(t => f.LastUsedUtc <= t.RemovedUtc)).ToArray();
            return (index with { Sessions = visible, HasReadErrors = index.HasReadErrors || snapshot.HasErrors }, visibleFolders);
        }
    }
}
