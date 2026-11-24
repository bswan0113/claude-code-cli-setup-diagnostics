using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace ClaudeLauncher;

public partial class MainWindow : Window
{
    private readonly List<LaunchOperation> _operations = [];
    private readonly List<(LauncherProfile Profile, SetupRequest Request)> _setupQueue = [];
    private LauncherProfile? _setupProfile;
    private readonly DispatcherTimer _activityTimer;
    private SetupPanel? _setup;
    private IInputElement? _returnFocus;
    private bool _initialized;
    private bool _closing;
    private bool _closeRequested;
    private bool BusyBeforeClose => _setup?.IsBusy == true || _changingSessions || _profileSigningIn || _profileSwitching;
    private int _nextTerminal;
    private string? _status;

    public MainWindow()
    {
        _appearance = AppearancePreferences.Load();
        AppearanceResources.Apply(_appearance);
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(SidebarWindowMessage);
            ApplyWindowAppearance();
        };
        _activityTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _activityTimer.Tick += ActivityTimer_Tick;
        RunningTerminals.ItemsSource = _liveRows;
        FolderSort.SelectedValue = _appearance.FolderOrder.ToString();
        LanguagePicker.SelectedValue = UiLanguage.Current;
        Language = XmlLanguage.GetLanguage(UiLanguage.Current);
        FitInitialWindow();
        _initialized = true;
        UpdateLayoutMode();
        RefreshHistoryView();
        RenderInput();
        Loaded += async (_, _) =>
        {
            if (_appearance.Sidebar)
                PinSidebar(_appearance.SidebarRight, _appearance.SidebarMonitor);
            InitProfileTabs();
            _activityTimer.Start();
            await Task.WhenAll(RefreshHistoriesAsync(), DiscoverAsync());
        };
    }

    private void LanguagePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized || LanguagePicker.SelectedValue is not string language)
            return;

        UiLanguage.Apply(language);
        InitProfileTabs();
        Language = XmlLanguage.GetLanguage(language);
        SetStatus(!UiLanguage.Save() ? "LanguageSaveFailed" : _status == "LanguageSaveFailed" ? null : _status);
        UpdateLayoutMode();
        RefreshLiveRows();
        RefreshHistoryView();
    }

    private void FolderInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_initialized)
            RenderInput();
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        if (ChooseFolder() is { } folder)
        {
            FolderInput.Text = folder;
            OpenButton.Focus();
        }
    }

    private async void SidebarNew_Click(object sender, RoutedEventArgs e)
    {
        if (ChooseFolder() is { } folder)
            await OpenInFolderAsync(folder);
    }

    private string? ChooseFolder()
    {
        var dialog = new OpenFolderDialog { Title = UiLanguage.Text("FolderLabel") };
        return dialog.ShowDialog(this) == true ? dialog.FolderName : null;
    }

    private async void FolderInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || !OpenButton.IsEnabled)
            return;

        e.Handled = true;
        await OpenInFolderAsync(FolderInput.Text);
    }

    private async void Open_Click(object sender, RoutedEventArgs e) =>
        await OpenInFolderAsync(FolderInput.Text);

    private async void StartInFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string folder } button)
        {
            SelectTreeRow(button);
            await OpenInFolderAsync(folder);
        }
    }

    private async void ResumeSession_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: SessionTreeRow row } button)
        {
            SelectTreeRow(button);
            await ResumeAsync(row.Session);
        }
    }

    private Task ResumeAsync(SavedSession session) =>
        LaunchAsync(new LaunchIntent(session.Cwd, LaunchMode.Resume, session.Id));

    private async void SessionPicker_Click(object sender, RoutedEventArgs e)
    {
        if (ChooseFolder() is { } folder)
            await OpenInFolderAsync(folder, LaunchMode.Picker);
    }

    private async void FallbackFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string folder } button)
        {
            SelectTreeRow(button);
            FolderInput.Text = folder;
            await OpenInFolderAsync(folder, LaunchMode.Picker);
        }
    }

    private Task OpenInFolderAsync(string input, LaunchMode mode = LaunchMode.New)
    {
        var folder = FullFolderPath(input);
        if (folder is not null)
            return LaunchAsync(new LaunchIntent(folder, mode));

        QueueSetup(new SetupRequest("", new LaunchIntent(input, mode), new DiagnosticNotice("InvalidFolder")));
        return Task.CompletedTask;
    }

    private static string? FullFolderPath(string input)
    {
        input = input.Trim();
        if (input.Length > 1 && input[0] == '"' && input[^1] == '"')
            input = input[1..^1];

        return WorkingFolder.Normalize(input);
    }

    private void FolderHeader_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: FolderTreeRow folder } element)
            return;

        e.Handled = true;
        if (e.ClickCount != 1)
            return;

        SelectTreeRow(element);
        folder.IsExpanded = !folder.IsExpanded;
    }

    private async void FolderTree_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.OriginalSource is ButtonBase)
            return;

        e.Handled = true;
        switch (FolderTree.SelectedItem)
        {
            case FolderTreeRow folder:
                folder.IsExpanded = !folder.IsExpanded;
                break;
            case SessionTreeRow session:
                await ResumeAsync(session.Session);
                break;
            case FallbackTreeRow fallback:
                FolderInput.Text = fallback.Path;
                await OpenInFolderAsync(fallback.Path, LaunchMode.Picker);
                break;
        }
    }

    private static void SelectTreeRow(FrameworkElement element)
    {
        for (DependencyObject? current = element; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is not TreeViewItem item)
                continue;
            item.IsSelected = true;
            item.Focus();
            return;
        }
    }

    private void FocusTerminal_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: LiveRow row })
            FocusTerminal(row);
    }

    private void FocusTerminal(LiveRow row)
    {
        try
        {
            if (row.Terminal.TryFocus())
            {
                SetStatus(null);
                return;
            }
        }
        catch (Exception exception)
        {
            Debug.WriteLine(exception.GetType().Name);
        }

        SetStatus("TerminalFocusUnavailable");
    }

    private static bool IsSelectedProfile(LauncherProfile profile) => profile.Id == LauncherProfiles.Selected.Id;
    private IEnumerable<LaunchOperation> SelectedOperations => _operations.Where(op => IsSelectedProfile(op.Profile));

    private async Task LaunchAsync(LaunchIntent intent, bool afterRepair = false, LauncherProfile? forProfile = null)
    {
        var profile = forProfile ?? LauncherProfiles.Selected;
        using var scope = LauncherProfiles.Bind(profile);
        if (_closing || !IsSelectedProfile(profile))
            return;

        if (intent.SessionId is Guid id)
        {
            var existing = FindOwnedLive(id, profile);
            if (existing is not null)
            {
                FocusTerminal(existing);
                return;
            }

            if (SelectedOperations.Any(operation => operation.PendingSession == id))
            {
                SetStatus("SessionOpening");
                return;
            }
        }

        var operation = new LaunchOperation(intent, ++_nextTerminal, profile);
        _operations.Add(operation);
        SetStatus(null);
        RefreshHistoryView();
        LaunchResult result;
        try
        {
            if (!await Task.Run(() => Directory.Exists(intent.Folder)))
            {
                QueueSetup(new SetupRequest(intent.Folder, intent, new DiagnosticNotice("InvalidFolder")), profile);
                return;
            }

            if (intent.SessionId is Guid requested && await FindLiveBeforeStartAsync(requested, profile) is { } active)
            {
                if (IsSelectedProfile(profile)) FocusTerminal(active);
                return;
            }
            if (_closing)
                return;

            result = await RunTerminalAsync(operation);
            if (!afterRepair && IsSelectedProfile(profile) && intent.Mode == LaunchMode.Resume && result is { Status: "Failed", ExitCode: not null })
            {
                var index = await RefreshHistoriesAsync(profile);
                if (index.FallbackFolders.Contains(intent.Folder, StringComparer.OrdinalIgnoreCase) &&
                    index.Sessions.All(session => session.Id != intent.SessionId) &&
                    await Task.Run(() => Directory.Exists(intent.Folder)) && IsSelectedProfile(profile))
                {
                    operation.Live = null;
                    operation.Intent = new LaunchIntent(intent.Folder, LaunchMode.Picker);
                    RefreshLiveRows();
                    SetStatus("SessionFallbackRunning");
                    result = await RunTerminalAsync(operation);
                }
            }
        }
        catch (Exception exception)
        {
            Debug.WriteLine(exception.GetType().Name);
            result = new LaunchResult("StartFailed", null);
        }
        finally
        {
            RememberOwnedSession(operation);
            _operations.Remove(operation);
            if (!_closing && IsSelectedProfile(profile) && (RefreshLiveRows() || intent.SessionId is not null))
                RefreshHistoryView();
        }

        if (_closing)
            return;

        switch (result.Status)
        {
            case "Exited":
                await Task.Run(() => FolderHistory.Remember(intent.Folder));
                if (IsSelectedProfile(profile)) await RefreshHistoriesAsync(profile);
                return;
            case "Canceled":
                if (IsSelectedProfile(profile)) SetStatus("TerminalCanceled");
                return;
            case "Unknown":
                return;
        }

        var effective = operation.Intent;
        if (result is { Status: "Failed", ExitCode: int exitCode })
        {
            QueueSetup(new SetupRequest(effective.Folder, effective, new DiagnosticNotice("CliExitedWithError", ExitCode: exitCode)), profile);
            return;
        }

        var failure = new DiagnosticNotice(afterRepair ? "RetryFailed" : "LaunchFailed", ExitCode: result.ExitCode);
        var examine = !afterRepair && (result.Status is "CommandMissing" or "StartFailed" ||
            result is { Status: "Failed", ExitCode: null });
        QueueSetup(new SetupRequest(effective.Folder, effective, failure, examine), profile);
    }

    private Task<LaunchResult> RunTerminalAsync(LaunchOperation operation)
    {
        var intent = operation.Intent;
        var attempt = ++operation.Attempt;
        return TerminalLauncher.LaunchAsync(intent.Folder, intent.SessionId, intent.Mode == LaunchMode.Picker, terminal =>
        {
            if (Dispatcher.CheckAccess())
                Started(terminal);
            else
                Dispatcher.BeginInvoke(() => Started(terminal));
        });

        void Started(LiveTerminal terminal)
        {
            using var scope = LauncherProfiles.Bind(operation.Profile);
            if (_closing || !_operations.Contains(operation) || operation.Attempt != attempt)
                return;
            operation.Live = new LiveRow(terminal, operation.Ordinal, owned: true);
            RememberOwnedSession(operation);
            if (!IsSelectedProfile(operation.Profile)) return;
            RefreshLiveRows();
            RefreshHistoryView();
        }
    }

    private LiveRow? FindOwnedLive(Guid id, LauncherProfile profile) => _operations.Where(op => op.Profile.Id == profile.Id)
        .Select(operation => operation.Live)
        .FirstOrDefault(row => row is not null && row.Terminal.SessionId == id);

    private async Task<LiveRow?> FindLiveBeforeStartAsync(Guid id, LauncherProfile profile)
    {
        // A click can arrive before the first poll or after another app starts a session.
        var observed = await Task.Run(LiveTerminal.Discover);
        var own = FindOwnedLive(id, profile);
        if (own is not null)
            return own;

        var terminal = observed.FirstOrDefault(candidate => candidate.SessionId == id);
        if (terminal is null)
            return null;
        if (!IsSelectedProfile(profile)) return new LiveRow(terminal, 0, owned: false);

        var row = UpdateExternalTerminal(terminal);
        RefreshLiveRows();
        RefreshHistoryView();
        return row;
    }

    private void Setup_Click(object sender, RoutedEventArgs e)
    {
        var input = FolderInput.Text;
        var folder = FullFolderPath(input);
        if (folder is null && !string.IsNullOrWhiteSpace(input))
        {
            QueueSetup(new SetupRequest("", new LaunchIntent(input), new DiagnosticNotice("InvalidFolder")));
            return;
        }

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var cwd = folder ?? (Directory.Exists(profile) ? profile : AppContext.BaseDirectory);
        QueueSetup(new SetupRequest(cwd, folder is null ? null : new LaunchIntent(folder), Examine: true));
    }

    private void QueueSetup(SetupRequest request, LauncherProfile? forProfile = null)
    {
        if (_closing)
            return;

        var profile = forProfile ?? LauncherProfiles.Selected;
        _setupQueue.Add((profile, request));
        if (_setup is null && IsSelectedProfile(profile))
            Dispatcher.BeginInvoke(OpenNextSetup);
    }

    private async void OpenNextSetup()
    {
        if (_closing || _setup is not null || _appearancePanel is not null)
            return;
        int next = _setupQueue.FindIndex(entry => IsSelectedProfile(entry.Profile));
        if (next < 0) return;
        var (profile, request) = _setupQueue[next];
        _setupQueue.RemoveAt(next);
        using var scope = LauncherProfiles.Bind(profile);

        var panel = new SetupPanel(request.Cwd, request.Intent, request.Examine ? null : request.Notice);
        _setup = panel;
        _setupProfile = profile;
        panel.Finished += FinishSetup;
        ShowOverlay(panel);
        await panel.OpenAsync(request.Examine, request.Notice);
    }

    private void FinishSetup(LaunchIntent? retry)
    {
        if (_setup is not { } panel)
            return;

        panel.Finished -= FinishSetup;
        var profile = _setupProfile;
        _setup = null;
        _setupProfile = null;
        HideOverlay();

        if (retry is not null && profile is not null)
            _ = LaunchAsync(retry, afterRepair: true, forProfile: profile);
        if (_setupQueue.Count > 0)
            Dispatcher.BeginInvoke(OpenNextSetup);
    }

    private void SetStatus(string? key)
    {
        _status = key;
        StatusText.Text = key is null ? "" : UiLanguage.Text(key);
        StatusText.Visibility = key is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void RenderInput()
    {
        var empty = string.IsNullOrWhiteSpace(FolderInput.Text);
        OpenButton.IsEnabled = !empty;
        FolderHint.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (BusyBeforeClose)
        {
            e.Cancel = true;
            if (!_closeRequested)
            {
                _closeRequested = true;
                SetResourceReference(TitleProperty, "ClosingAfterWork");
                _profileLoginCancellation?.Cancel();
                _setup?.CancelPendingReadOrLogin();
            }
            return;
        }
        _closing = true;
        if (IsSidebar && WindowState == WindowState.Normal)
            SaveSidebar();
        _usageRead?.Cancel();
        _activityTimer.Stop();
    }

    private sealed record SetupRequest(string Cwd, LaunchIntent? Intent, DiagnosticNotice? Notice = null,
        bool Examine = false);

    private sealed class LaunchOperation(LaunchIntent intent, int ordinal, LauncherProfile profile)
    {
        public LauncherProfile Profile { get; } = profile;
        public LaunchIntent Intent { get; set; } = intent;
        public Guid? PendingSession => Live is null ? Intent.SessionId : null;
        public int Ordinal { get; } = ordinal;
        public int Attempt { get; set; }
        public LiveRow? Live { get; set; }
        public Guid? RememberedSession { get; set; }
    }
}
