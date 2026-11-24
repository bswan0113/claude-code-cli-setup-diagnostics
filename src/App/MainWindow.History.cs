using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace ClaudeLauncher;

public partial class MainWindow
{
    private readonly ObservableCollection<LiveRow> _liveRows = [];
    private readonly Dictionary<int, LiveRow> _externalRows = [];
    private readonly HashSet<string> _expandedFolders = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<Guid> _activeIds = [];
    private SessionIndex? _sessions;
    private IReadOnlyList<RecentFolder> _folderHistory = [];
    private IReadOnlyList<FolderTreeRow> _shownFolders = [];
    private string _renderedSearch = "";
    private string? _selectedTreeKey;
    private DateTime _lastTitleLookup;
    private bool _restoringTree;
    private int? _discoveryGeneration;
    private int _profileViewGeneration;
    private bool _folderHistoryUnavailable;
    private int _historyVersion;

    private async Task ResetProfileHistoryAsync()
    {
        var profile = LauncherProfiles.Selected;
        using var scope = LauncherProfiles.Bind(profile);
        _profileViewGeneration++;
        _historyVersion++;
        _usageRead?.Cancel();
        _usageRead = null;
        _usageRescanQueued = false;
        _nextUsagePoll = default;
        _lastTitleLookup = default;
        _externalRows.Clear();
        _liveRows.Clear();
        _activeIds.Clear();
        _sessions = null;
        _folderHistory = [];
        _folderHistoryUnavailable = false;
        _shownFolders = [];
        _expandedFolders.Clear();
        _renderedSearch = "";
        _restoringTree = false;
        FolderTree.ItemsSource = null;
        _selectedTreeKey = null;
        HistorySearch.Clear();
        FolderInput.Clear();
        RefreshLiveRows();
        RefreshHistoryView();
        await Task.WhenAll(RefreshHistoriesAsync(profile), DiscoverAsync());
        if (IsSelectedProfile(profile) && _setupQueue.Count != 0)
            _ = Dispatcher.BeginInvoke(OpenNextSetup);
    }

    private async Task<SessionIndex> RefreshHistoriesAsync(LauncherProfile? forProfile = null)
    {
        var profile = forProfile ?? LauncherProfiles.Selected;
        using var scope = LauncherProfiles.Bind(profile);
        var generation = _profileViewGeneration;
        var version = IsSelectedProfile(profile) ? ++_historyVersion : -1;
        SessionIndex index;
        IReadOnlyList<RecentFolder>? folders;
        try
        {
            (index, folders) = await ProjectRemoval.LoadVisibleAsync();
        }
        catch (Exception error)
        {
            Debug.WriteLine(error.GetType().Name);
            index = IsSelectedProfile(profile) && generation == _profileViewGeneration && _sessions is { } previousIndex
                ? previousIndex with { HasReadErrors = true } : new([], true);
            folders = null;
        }
        if (_closing || !IsSelectedProfile(profile) || generation != _profileViewGeneration || version != _historyVersion)
            return index;

        var previous = _sessions?.Sessions.ToDictionary(session => session.Id);
        if (previous is not null)
            index = index with { Sessions = index.Sessions.Select(session =>
                previous.TryGetValue(session.Id, out var old) && old.ModifiedUtc == session.ModifiedUtc &&
                string.Equals(old.TranscriptPath, session.TranscriptPath, StringComparison.OrdinalIgnoreCase)
                    ? session with { Usage = old.Usage } : session).ToArray() };
        _sessions = index;
        _folderHistoryUnavailable = folders is null;
        if (folders is not null)
            _folderHistory = folders;
        RefreshLiveRows();
        RefreshHistoryView();
        ScheduleUsageRead(liveOnly: false);
        return index;
    }

    private async void ActivityTimer_Tick(object? sender, EventArgs e)
    {
        if (_closeRequested)
        {
            if (!BusyBeforeClose && !_closing) Close();
            return;
        }
        var profile = LauncherProfiles.Selected;
        using var scope = LauncherProfiles.Bind(profile);
        foreach (var op in _operations.Where(op => op.Profile.Id != profile.Id))
            RememberOwnedSession(op);
        if (RefreshLiveRows())
            RefreshHistoryView();
        await DiscoverAsync();
        if (!_closing && IsSelectedProfile(profile) && _usageRead is null && _activeIds.Count != 0 && DateTime.UtcNow >= _nextUsagePoll)
            ScheduleUsageRead(liveOnly: true);
    }

    private async Task DiscoverAsync()
    {
        var profile = LauncherProfiles.Selected;
        using var scope = LauncherProfiles.Bind(profile);
        var generation = _profileViewGeneration;
        if (_discoveryGeneration == generation || _closing)
            return;

        _discoveryGeneration = generation;
        try
        {
            var discovered = await Task.Run(LiveTerminal.Discover);
            if (_closing || !IsSelectedProfile(profile) || generation != _profileViewGeneration)
                return;

            var observed = new HashSet<int>();
            foreach (var terminal in discovered)
            {
                observed.Add(terminal.ProcessId);
                UpdateExternalTerminal(terminal);
            }

            foreach (var stale in _externalRows.Keys.Where(pid => !observed.Contains(pid)).ToArray())
                _externalRows.Remove(stale);

            if (RefreshLiveRows())
                RefreshHistoryView();

            var now = DateTime.UtcNow;
            if (now - _lastTitleLookup > TimeSpan.FromSeconds(8) &&
                _liveRows.Any(row => row.SessionId is Guid id && FindSession(id) is null))
            {
                _lastTitleLookup = now;
                await RefreshHistoriesAsync(profile);
            }
        }
        catch (Exception exception)
        {
            Debug.WriteLine(exception.GetType().Name);
        }
        finally
        {
            if (_discoveryGeneration == generation) _discoveryGeneration = null;
        }
    }

    private LiveRow UpdateExternalTerminal(LiveTerminal terminal)
    {
        _externalRows.TryGetValue(terminal.ProcessId, out var row);
        var previous = row?.Terminal;
        if (terminal.SessionId is Guid id &&
            !SelectedOperations.Any(operation => operation.Live?.Terminal.ProcessId == terminal.ProcessId))
        {
            if (previous is null || previous.StartedUtc != terminal.StartedUtc)
                SessionRecency.Remember(id, terminal.StartedUtc);
            else if (previous.SessionId != id)
                SessionRecency.Remember(id, DateTime.UtcNow);
        }

        if (row is null)
            _externalRows.Add(terminal.ProcessId, row = new LiveRow(terminal, 0, owned: false));
        else
            row.UseTerminal(terminal);
        return row;
    }

    private SavedSession? FindSession(Guid id) => _sessions?.Sessions.FirstOrDefault(session => session.Id == id);

    private static void RememberOwnedSession(LaunchOperation operation)
    {
        if (operation.Live is not { } row || (row.Terminal.SessionId ?? operation.Intent.SessionId) is not Guid id ||
            operation.RememberedSession == id) return;
        using var scope = LauncherProfiles.Bind(operation.Profile);
        SessionRecency.Remember(id);
        operation.RememberedSession = id;
    }

    private bool RefreshLiveRows()
    {
        using var scope = LauncherProfiles.Bind(LauncherProfiles.Selected);
        var rows = new List<LiveRow>();
        var claimedIds = new HashSet<Guid>();
        var claimedProcesses = new HashSet<int>();
        foreach (var operation in SelectedOperations)
        {
            if (operation.Live is not { } row)
                continue;

            var id = row.Terminal.SessionId ?? operation.Intent.SessionId;
            RememberOwnedSession(operation);

            AppendLiveRow(row, id, rows, claimedIds, claimedProcesses);
        }

        foreach (var row in _externalRows.Values)
            AppendLiveRow(row, row.Terminal.SessionId, rows, claimedIds, claimedProcesses);

        var changed = !_activeIds.SetEquals(claimedIds) || !_liveRows.SequenceEqual(rows);
        _activeIds.Clear();
        _activeIds.UnionWith(claimedIds);
        for (var i = _liveRows.Count - 1; i >= 0; i--)
        {
            if (!rows.Contains(_liveRows[i]))
                _liveRows.RemoveAt(i);
        }

        for (var i = 0; i < rows.Count; i++)
        {
            var current = _liveRows.IndexOf(rows[i]);
            if (current < 0)
                _liveRows.Insert(i, rows[i]);
            else if (current != i)
                _liveRows.Move(current, i);
        }

        RunningCount.Text = rows.Count.ToString(CultureInfo.GetCultureInfo(UiLanguage.Current));
        RunningEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        return changed;
    }

    private void AppendLiveRow(LiveRow row, Guid? id, List<LiveRow> rows, HashSet<Guid> ids, HashSet<int> processes)
    {
        if (row.Terminal.IsSubagent || WorkingFolder.IsClaudeScratchpad(row.Terminal.Folder) ||
            id is Guid knownId && _sessions?.SubagentIds.Contains(knownId) == true)
            return;
        if (!processes.Add(row.Terminal.ProcessId) || id is Guid session && !ids.Add(session))
            return;

        var savedSession = id is Guid known ? FindSession(known) : null;
        row.Update(id, savedSession?.Title ?? UiLanguage.Text("UnnamedTerminal"), row.Terminal.Folder,
            row.Terminal.Activity, savedSession);
        rows.Add(row);
    }

    private void HistorySearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_initialized)
            return;

        UpdateLayoutMode();
        RefreshTree();
    }

    private void RefreshHistoryView()
    {
        using var scope = LauncherProfiles.Bind(LauncherProfiles.Selected);
        RefreshTree();
        var eligible = (_sessions?.Sessions ?? []).Where(session => !_activeIds.Contains(session.Id) &&
            !SelectedOperations.Any(operation => operation.PendingSession == session.Id));
        var recent = SessionRecency.Order(eligible).Take(5)
            .Select(session => new SessionTreeRow(session, SessionRecency.LastUsedUtc(session), running: null))
            .ToArray();
        RecentSessions.ItemsSource = recent;
        RecentCount.Text = recent.Length.ToString(CultureInfo.GetCultureInfo(UiLanguage.Current));
        RecentEmpty.Visibility = recent.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (IsSidebar)
            UpdateLayoutMode();
    }

    private void FolderSort_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized || FolderSort.SelectedValue is not string tag ||
            !Enum.TryParse<FolderSortOrder>(tag, out var order) || order == _appearance.FolderOrder)
            return;

        _appearance = _appearance with { FolderOrder = order };
        if (!_appearance.Save())
            SetStatus("FolderSortSaveFailed");
        else if (_status == "FolderSortSaveFailed")
            SetStatus(null);
        RefreshTree();
    }

    private IEnumerable<RecentFolder> OrderFolders(IEnumerable<RecentFolder> candidates)
    {
        var paths = StringComparer.OrdinalIgnoreCase;
        var names = StringComparer.Create(CultureInfo.GetCultureInfo(UiLanguage.Current), ignoreCase: true);
        // A folder may appear in several histories. Keep its most recent timestamp in every sort mode.
        var folders = candidates.GroupBy(row => row.Path, paths)
            .Select(group => group.MaxBy(row => row.LastUsedUtc)!);
        var sorted = _appearance.FolderOrder switch
        {
            FolderSortOrder.RecentAscending => folders.OrderBy(row => row.LastUsedUtc == DateTime.MinValue)
                .ThenBy(row => row.LastUsedUtc),
            FolderSortOrder.NameAscending => folders.OrderBy(row => LiveRow.FolderName(row.Path), names),
            FolderSortOrder.NameDescending => folders.OrderByDescending(row => LiveRow.FolderName(row.Path), names),
            FolderSortOrder.PathAscending => folders.OrderBy(row => row.Path, paths),
            FolderSortOrder.PathDescending => folders.OrderByDescending(row => row.Path, paths),
            _ => folders.OrderByDescending(row => row.LastUsedUtc)
        };
        return sorted.ThenBy(row => row.Path, paths);
    }

    private void RefreshTree()
    {
        using var scope = LauncherProfiles.Bind(LauncherProfiles.Selected);
        var query = HistorySearch.Text.Trim();
        if (_renderedSearch.Length == 0)
        {
            foreach (var folder in _shownFolders)
            {
                if (folder.IsExpanded)
                    _expandedFolders.Add(folder.Path);
                else
                    _expandedFolders.Remove(folder.Path);
            }
        }

        var previous = _shownFolders.ToDictionary(row => row.Path, StringComparer.OrdinalIgnoreCase);
        if (FolderTree.SelectedItem is TreeRow selected && !_restoringTree)
            _selectedTreeKey = selected.Key;

        var sessions = _sessions?.Sessions ?? [];
        var damaged = _sessions?.FallbackFolders ?? [];
        var index = SessionRecency.Order(sessions).ToLookup(session => session.Cwd, StringComparer.OrdinalIgnoreCase);
        var liveById = _liveRows.Where(row => row.SessionId.HasValue).ToDictionary(row => row.SessionId!.Value);
        var fallbacks = damaged.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var folders = _folderHistory.Concat(sessions.Select(session => new RecentFolder(session.Cwd, SessionRecency.LastUsedUtc(session))))
            .Concat(damaged.Select(path => new RecentFolder(path, DateTime.MinValue)))
            .Concat(_liveRows.Select(row => new RecentFolder(row.Terminal.Folder, row.Terminal.StartedUtc)));
        var results = new List<FolderTreeRow>();
        foreach (var folder in OrderFolders(folders))
        {
            var pathMatches = query.Length == 0 || Matches(folder.Path, query);
            var saved = index[folder.Path].ToArray();
            var children = saved.Where(session => pathMatches || Matches(session.Title, query))
                .Select(session => (TreeRow)new SessionTreeRow(session, SessionRecency.LastUsedUtc(session),
                    liveById.GetValueOrDefault(session.Id))).ToList();
            if (pathMatches && fallbacks.Contains(folder.Path))
                children.Add(new FallbackTreeRow(folder.Path));
            if (children.Count == 0 && !pathMatches)
                continue;
            if (children.Count == 0)
                children.Add(new EmptyTreeRow(folder.Path));

            var expanded = query.Length == 0 ? _expandedFolders.Contains(folder.Path)
                : _renderedSearch != query || !previous.TryGetValue(folder.Path, out var old) || old.IsExpanded;
            results.Add(new FolderTreeRow(folder.Path, children, saved, fallbacks.Contains(folder.Path), expanded));
        }

        _renderedSearch = query;
        _shownFolders = results;
        FolderTree.ItemsSource = results;
        var toSelect = results.SelectMany(folder => folder.Children.Prepend(folder))
            .FirstOrDefault(row => string.Equals(row.Key, _selectedTreeKey, StringComparison.OrdinalIgnoreCase));
        if (toSelect is not null)
        {
            _restoringTree = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                if (ReferenceEquals(_shownFolders, results))
                {
                    toSelect.IsSelected = true;
                    _restoringTree = false;
                }
            });
        }
        else
            _restoringTree = false;

        SearchHint.Visibility = query.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        HistoryNotice.Visibility = results.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        HistoryNotice.Text = UiLanguage.Text(query.Length > 0 ? "NoSearchResults" :
            _sessions is null ? "HistoryLoading" : _folderHistoryUnavailable ? "HistoryUnavailable" : "HistoryEmpty");
    }

    private static bool Matches(string text, string query) =>
        text.Contains(query, StringComparison.CurrentCultureIgnoreCase);
}
