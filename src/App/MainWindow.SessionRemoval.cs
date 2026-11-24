using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace ClaudeLauncher;

public partial class MainWindow
{
    private bool _changingSessions;

    private static string TrashText(string key, params object[] args) =>
        string.Format(CultureInfo.GetCultureInfo(UiLanguage.Current), UiLanguage.Text("SessionTrash" + key), args);

    private static bool RemovalAccessError(Exception error) => error is System.IO.IOException or
        System.IO.InvalidDataException or UnauthorizedAccessException or InvalidOperationException or
        System.Security.SecurityException or System.Text.Json.JsonException or Win32Exception or
        ArgumentException or NotSupportedException or WaitHandleCannotBeOpenedException;

    private void SavedSessionMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is ContextMenu menu)
            menu.Tag = (menu.PlacementTarget as FrameworkElement)?.DataContext;
    }

    private void SavedSessionMore_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { ContextMenu: { } menu } button)
            return;
        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private async void MoveSavedSession_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem item && ItemsControl.ItemsControlFromItemContainer(item) is ContextMenu menu)
            await RemoveRowAsync(menu.Tag);
    }

    private async void SavedSession_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete || Keyboard.Modifiers != ModifierKeys.None ||
            Keyboard.FocusedElement is not FrameworkElement { DataContext: SessionTreeRow or FolderTreeRow } target)
            return;
        e.Handled = true;
        await RemoveRowAsync(target.DataContext);
    }

    private Task RemoveRowAsync(object? row) => row switch
    {
        SessionTreeRow session => ReviewRemovalAsync(this, [], [session.Session]),
        FolderTreeRow folder => ReviewRemovalAsync(this, [folder.Path], []),
        _ => Task.CompletedTask
    };

    private bool SelectionIsActive(IReadOnlyList<string> folders, IReadOnlyList<SavedSession> sessions)
    {
        var paths = folders.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ids = sessions.Select(session => session.Id).ToHashSet();
        return ids.Overlaps(_activeIds) || _liveRows.Any(row => paths.Contains(row.Terminal.Folder)) ||
            _operations.Any(op => paths.Contains(op.Intent.Folder) ||
                op.PendingSession is Guid pending && ids.Contains(pending) ||
                op.Live?.SessionId is Guid live && ids.Contains(live) ||
                op.RememberedSession is Guid remembered && ids.Contains(remembered));
    }

    private async Task<bool?> ReviewRemovalAsync(Window owner, IReadOnlyList<string> folders,
        IReadOnlyList<SavedSession> sessions)
    {
        if (_changingSessions || _closing || folders.Count + sessions.Count == 0)
            return false;
        _changingSessions = true;
        try
        {
            if (SelectionIsActive(folders, sessions))
                return RemovalProblem(owner, "Active");

            owner.IsEnabled = false;
            var preview = await ProjectRemoval.PreviewBatchAsync(folders, sessions);
            owner.IsEnabled = true;
            if (!owner.IsVisible || _closing)
                return false;
            if (preview.Code != "Ready" || SelectionIsActive(preview.Folders, preview.Sessions))
                return RemovalProblem(owner, preview.Code == "Ready" ? "Active" : preview.Code);

            var folderPaths = preview.Folders.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var details = preview.Folders.Select(path =>
                TrashText("ProjectLine", path, preview.Sessions.Count(session =>
                    string.Equals(path, session.Cwd, StringComparison.OrdinalIgnoreCase))))
                .Concat(preview.Sessions.Where(session => !folderPaths.Contains(session.Cwd))
                    .Select(session => $"{session.Title}\n{session.Cwd}  ·  {session.Id:D}"));
            var body = TrashText("ReviewCount", preview.Folders.Count, preview.Sessions.Count) + "\n" +
                TrashText(preview.Folders.Count > 0 ? "ProjectScope" : "Scope");
            if (!ShowRemovalPrompt(owner, "Review", body, details, "Confirm"))
                return null;
            if (SelectionIsActive(preview.Folders, preview.Sessions))
                return RemovalProblem(owner, "Active");

            owner.IsEnabled = false;
            var result = preview.Folders.Count == 0 && preview.Sessions.Count == 1
                ? await SessionRemoval.RemoveAsync(preview.Sessions[0])
                : await ProjectRemoval.RemoveAsync(preview);
            if (owner == this)
                await RefreshHistoriesAsync();
            owner.IsEnabled = true;
            if (result.Code != "Removed")
                return RemovalProblem(owner, result.Code);
            SetStatus("SessionTrashRemoved");
            return true;
        }
        catch (Exception ex) when (RemovalAccessError(ex))
        {
            owner.IsEnabled = true;
            return RemovalProblem(owner, "Unavailable");
        }
        finally
        {
            owner.IsEnabled = true;
            _changingSessions = false;
        }
    }

    private bool RemovalProblem(Window owner, string code)
    {
        if (!_closing && owner.IsVisible)
        {
            string key = UiLanguage.TryText("SessionTrash" + code) is null ? "Unavailable" : code;
            ShowRemovalPrompt(owner, "Attention", TrashText(key), [], null);
        }
        return false;
    }

    private void ManageSavedSessions_Click(object sender, RoutedEventArgs e)
    {
        if (_changingSessions)
            return;
        var window = RemovalWindow(this, "Manage", 680, 610);
        var layout = new DockPanel { Margin = new Thickness(16) };
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        DockPanel.SetDock(actions, Dock.Bottom);
        layout.Children.Add(actions);
        var note = RemovalText("", "BodyText");
        note.Margin = new Thickness(0, 7, 0, 0);
        DockPanel.SetDock(note, Dock.Bottom);
        layout.Children.Add(note);
        var tabs = new TabControl();
        tabs.SetResourceReference(Control.BackgroundProperty, "Surface");
        layout.Children.Add(tabs);

        var select = new DockPanel { Margin = new Thickness(6) };
        var hint = RemovalText(TrashText("SelectionHint"), "BodyText");
        hint.Margin = new Thickness(4, 2, 4, 8);
        DockPanel.SetDock(hint, Dock.Top);
        select.Children.Add(hint);
        var tree = new TreeView { ItemTemplate = (DataTemplate)FindResource("RemovalChoiceTemplate") };
        tree.SetResourceReference(Control.BackgroundProperty, "Surface");
        tree.SetResourceReference(Control.ForegroundProperty, "Ink");
        VirtualizingPanel.SetIsVirtualizing(tree, true);
        VirtualizingPanel.SetVirtualizationMode(tree, VirtualizationMode.Recycling);
        ScrollViewer.SetHorizontalScrollBarVisibility(tree, ScrollBarVisibility.Disabled);
        select.Children.Add(tree);
        tabs.Items.Add(new TabItem { Header = TrashText("SelectTab"), Content = select });

        var trash = new ListBox { Margin = new Thickness(6), ItemTemplate = (DataTemplate)FindResource("RemovedChoiceTemplate") };
        trash.SetResourceReference(Control.BackgroundProperty, "Surface");
        ScrollViewer.SetHorizontalScrollBarVisibility(trash, ScrollBarVisibility.Disabled);
        tabs.Items.Add(new TabItem { Header = TrashText("Title"), Content = trash });
        tabs.SelectedIndex = 0;

        var proceed = RemovalButton("Review", "PrimaryAction");
        var close = RemovalButton("Close", "Action");
        close.IsCancel = true;
        actions.Children.Add(proceed);
        actions.Children.Add(close);
        IReadOnlyList<RemovalChoice> roots = [];
        IReadOnlyList<TrashEntry> entries = [];
        bool loading = true;
        bool historyError = false;
        bool trashError = false;
        string? completion = null;
        void ApplyState()
        {
            if (loading)
            {
                proceed.IsEnabled = false;
                note.Text = TrashText("Loading");
                return;
            }
            if (tabs.SelectedIndex == 0)
            {
                int projects = roots.Count(row => row.Marked);
                int count = roots.Sum(row => row.Marked ? row.Children.Count : row.Children.Count(child => child.Marked));
                proceed.Content = TrashText("Review");
                proceed.IsEnabled = !historyError && projects + count > 0;
                note.Text = historyError ? TrashText("Unavailable") : TrashText("ReviewCount", projects, count);
            }
            else
            {
                proceed.Content = TrashText("Restore");
                proceed.IsEnabled = trash.SelectedItem is TrashEntry;
                note.Text = trashError ? TrashText("Unavailable") :
                    entries.Count == 0 ? TrashText("Empty") : TrashText("RestoreHint");
            }
            if (completion is not null)
                note.Text = TrashText(completion) + "\n" + note.Text;
        }
        async Task ReloadAsync()
        {
            loading = true;
            ApplyState();
            try
            {
                var index = await RefreshHistoriesAsync();
                if (!window.IsVisible || _closing)
                    return;
                var sessionsByFolder = index.Sessions.ToLookup(session => session.Cwd, StringComparer.OrdinalIgnoreCase);
                var folders = _folderHistory.Select(row => row.Path).Concat(sessionsByFolder.Select(group => group.Key))
                    .Concat(index.FallbackFolders).Concat(_liveRows.Select(row => row.Terminal.Folder))
                    .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.CurrentCultureIgnoreCase);
                roots = folders.Select(path => new RemovalChoice(path, sessionsByFolder[path], ApplyState)).ToArray();
                tree.ItemsSource = roots;
                historyError = false;
            }
            catch (Exception ex) when (RemovalAccessError(ex))
            {
                roots = [];
                tree.ItemsSource = roots;
                historyError = true;
            }
            IReadOnlyList<RemovedSession> singles = [];
            IReadOnlyList<RemovedProject> groups = [];
            trashError = false;
            try { singles = await SessionRemoval.ListRemovedAsync(); }
            catch (Exception ex) when (RemovalAccessError(ex)) { trashError = true; }
            try { groups = await ProjectRemoval.ListRemovedAsync(); }
            catch (Exception ex) when (RemovalAccessError(ex)) { trashError = true; }
            if (window.IsVisible && !_closing)
            {
                entries = singles.Select(row => new TrashEntry(row.Title, row.Cwd + "  ·  " + row.Id, row.RemovedUtc, row, null))
                    .Concat(groups.Select(group => new TrashEntry(TrashText(group.NeedsRecovery ? "RecoveryGroup" : "ReviewCount",
                            group.Folders.Count, group.SessionCount),
                        string.Join("\n", group.Folders.Concat(group.SessionFolders).Distinct(StringComparer.OrdinalIgnoreCase)),
                        group.RemovedUtc, null, group)))
                    .OrderByDescending(row => row.RemovedUtc).ToArray();
                trash.ItemsSource = entries;
                loading = false;
                ApplyState();
            }
        }
        tabs.SelectionChanged += (_, _) => ApplyState();
        trash.SelectionChanged += (_, _) => ApplyState();
        proceed.Click += async (_, _) =>
        {
            if (loading || _changingSessions)
                return;
            completion = null;
            if (tabs.SelectedIndex == 0)
            {
                var selectedFolders = roots.Where(row => row.Marked).Select(row => row.Path).ToArray();
                var selectedSessions = roots.Where(row => !row.Marked).SelectMany(row => row.Children)
                    .Where(row => row.Marked).Select(row => row.Session!).ToArray();
                var removed = await ReviewRemovalAsync(window, selectedFolders, selectedSessions);
                if (removed is null)
                    return;
                if (removed == true)
                    completion = "Removed";
            }
            else if (trash.SelectedItem is TrashEntry entry)
            {
                var restored = await RestoreEntryAsync(window, entry);
                if (restored is null)
                    return;
                if (restored == true)
                    completion = "Restored";
            }
            if (window.IsVisible && !_closing)
                await ReloadAsync();
        };
        window.Loaded += async (_, _) => await ReloadAsync();
        window.Content = layout;
        window.ShowDialog();
    }

    private async Task<bool?> RestoreEntryAsync(Window owner, TrashEntry entry)
    {
        if (!ShowRemovalPrompt(owner, "Restore", TrashText("RestoreHint"), [entry.Label, entry.Detail], "Restore"))
            return null;
        _changingSessions = true;
        owner.IsEnabled = false;
        try
        {
            var result = entry.Single is { } single ? await SessionRemoval.RestoreAsync(single)
                : await ProjectRemoval.RestoreAsync(entry.Group!);
            owner.IsEnabled = true;
            if (result.Code != "Restored")
                return RemovalProblem(owner, result.Code);
            SetStatus("SessionTrashRestored");
            return true;
        }
        catch (Exception ex) when (RemovalAccessError(ex))
        {
            owner.IsEnabled = true;
            return RemovalProblem(owner, "Unavailable");
        }
        finally
        {
            owner.IsEnabled = true;
            _changingSessions = false;
        }
    }

    private Window RemovalWindow(Window owner, string titleKey, double width, double height)
    {
        var window = new Window
        {
            Title = TrashText(titleKey), Owner = owner, ShowInTaskbar = false, Language = Language,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, FontFamily = FontFamily,
            Width = Math.Min(width, SystemParameters.WorkArea.Width),
            Height = Math.Min(height, SystemParameters.WorkArea.Height),
            MinWidth = Math.Min(320, SystemParameters.WorkArea.Width), MinHeight = Math.Min(280, SystemParameters.WorkArea.Height)
        };
        window.SetResourceReference(Control.BackgroundProperty, "Canvas");
        window.SetResourceReference(Control.ForegroundProperty, "Ink");
        window.SetResourceReference(Control.FontSizeProperty, "Font16");
        return window;
    }

    private TextBlock RemovalText(string text, string style) => new()
        { Text = text, Style = (Style)FindResource(style), TextWrapping = TextWrapping.Wrap };

    private Button RemovalButton(string key, string style) => new()
        { Content = TrashText(key), Style = (Style)FindResource(style), Margin = new Thickness(4, 0, 0, 4) };

    private bool ShowRemovalPrompt(Window owner, string heading, string body, IEnumerable<string> details, string? action)
    {
        var dialog = RemovalWindow(owner, heading, 530, 345);
        var panel = new DockPanel { Margin = new Thickness(20) };
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        DockPanel.SetDock(buttons, Dock.Bottom);
        panel.Children.Add(buttons);
        var cancel = RemovalButton(action is null ? "Close" : "Cancel", "Action");
        cancel.IsCancel = true;
        cancel.IsDefault = true;
        if (action is not null)
        {
            var confirm = RemovalButton(action, "PrimaryAction");
            confirm.Click += (_, _) => dialog.DialogResult = true;
            buttons.Children.Add(confirm);
        }
        buttons.Children.Add(cancel);
        cancel.Click += (_, _) => { if (dialog.IsVisible) dialog.DialogResult = false; };
        dialog.ContentRendered += (_, _) => cancel.Focus();
        var content = new StackPanel();
        content.Children.Add(RemovalText(body, "BodyText"));
        foreach (var line in details)
        {
            var label = RemovalText(line, "BodyText");
            label.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
            label.Margin = new Thickness(0, 10, 0, 0);
            content.Children.Add(label);
        }
        panel.Children.Add(new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        dialog.Content = panel;
        return dialog.ShowDialog() == true;
    }

    private sealed class RemovalChoice : INotifyPropertyChanged
    {
        private readonly Action _changed;
        private readonly RemovalChoice? _parent;
        private bool _marked;
        public string Label { get; }
        public string Detail { get; }
        public string Path { get; }
        public SavedSession? Session { get; }
        public IReadOnlyList<RemovalChoice> Children { get; }
        public bool Selectable => _parent?.Marked != true;
        public bool Marked
        {
            get => _marked || _parent?.Marked == true;
            set
            {
                if (_marked == value || !Selectable)
                    return;
                _marked = value;
                PropertyChanged?.Invoke(this, new(nameof(Marked)));
                foreach (var child in Children)
                {
                    child.PropertyChanged?.Invoke(child, new(nameof(Selectable)));
                    child.PropertyChanged?.Invoke(child, new(nameof(Marked)));
                }
                _changed();
            }
        }

        public RemovalChoice(string path, IEnumerable<SavedSession> sessions, Action changed)
        {
            Path = path;
            _changed = changed;
            Children = sessions.Select(session => new RemovalChoice(session, this, changed)).ToArray();
            Label = TrashText("ProjectLine", LiveRow.FolderName(path), Children.Count);
            Detail = path;
        }

        private RemovalChoice(SavedSession session, RemovalChoice parent, Action changed)
        {
            _parent = parent;
            _changed = changed;
            Session = session;
            Path = session.Cwd;
            Label = session.Title;
            Detail = session.Id.ToString("D");
            Children = [];
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private sealed record TrashEntry(string Label, string Paths, DateTime RemovedUtc, RemovedSession? Single, RemovedProject? Group)
    {
        public string Detail => RemovedUtc.ToLocalTime().ToString("g", CultureInfo.GetCultureInfo(UiLanguage.Current)) + "\n" + Paths;
    }
}
