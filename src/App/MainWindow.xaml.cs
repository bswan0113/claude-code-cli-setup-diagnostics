using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using Microsoft.Win32;

namespace ClaudeLauncher;

public partial class MainWindow : Window
{
    private const string LoginCommand = "claude auth login";
    private bool _initialized;
    private LaunchIntent? _activeLaunch;
    private SessionIndex? _sessions;
    private IReadOnlyList<RecentFolder>? _folderHistory;
    private bool _folderHistoryUnavailable;
    private bool _showAllSessions;
    private string? _workStatus;
    private string? _status;
    private Notice? _notice;
    private int _manualCheckCount;
    private int _historyLoadCount;
    private int _sessionLoadCount;
    private bool TerminalPending => _activeLaunch is not null;

    public MainWindow()
    {
        InitializeComponent();
        LanguagePicker.SelectedValue = UiLanguage.Current;
        Language = XmlLanguage.GetLanguage(UiLanguage.Current);
        _initialized = true;
        Loaded += async (_, _) => await Task.WhenAll(ReloadSessionsAsync(), LoadHistoryAsync());
        RenderSessions();
        Render();
    }

    private void LanguagePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized || LanguagePicker.SelectedValue is not string language)
            return;

        UiLanguage.Apply(language);
        Language = XmlLanguage.GetLanguage(language);
        if (!UiLanguage.Save())
            _status = "LanguageSaveFailed";
        else if (_status == "LanguageSaveFailed")
            _status = null;

        RenderSessions();
        Render();
    }

    private void FolderInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_initialized)
            return;

        _notice = null;
        _status = null;
        _showAllSessions = false;
        SetupScroll.ScrollToTop();
        RenderSessions();
        Render();
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        if (ChooseFolder())
            OpenButton.Focus();
    }

    private bool ChooseFolder()
    {
        var dialog = new OpenFolderDialog { Title = UiLanguage.Text("FolderLabel") };
        if (dialog.ShowDialog(this) != true)
            return false;

        FolderInput.Text = dialog.FolderName;
        return true;
    }

    private async void FolderInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || !OpenButton.IsEnabled)
            return;

        e.Handled = true;
        await OpenFolderAsync();
    }

    private async void Open_Click(object sender, RoutedEventArgs e) => await OpenFolderAsync();

    private async void RecentFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_workStatus is not null || TerminalPending || sender is not Button { Tag: string folder })
            return;

        FolderInput.Text = folder;
        await OpenFolderAsync();
    }

    private async void Session_Click(object sender, RoutedEventArgs e)
    {
        if (_workStatus is not null || TerminalPending || sender is not Button { Tag: SavedSession session })
            return;

        FolderInput.Text = session.Cwd;
        if (TryGetFolder(out var folder) && folder is not null)
            await LaunchAsync(new LaunchIntent(folder, LaunchMode.Resume, session.Id));
    }

    private void ShowAllSessions_Click(object sender, RoutedEventArgs e)
    {
        _showAllSessions = !_showAllSessions;
        RenderSessions();
    }

    private async void SessionPicker_Click(object sender, RoutedEventArgs e)
    {
        if (_workStatus is not null || TerminalPending)
            return;

        if (string.IsNullOrWhiteSpace(FolderInput.Text) && !ChooseFolder())
            return;

        if (TryGetFolder(out var folder) && folder is not null)
            await LaunchAsync(new LaunchIntent(folder, LaunchMode.Picker));
    }

    private async void FallbackFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_workStatus is not null || TerminalPending || sender is not Button { Tag: string folder })
            return;

        FolderInput.Text = folder;
        if (TryGetFolder(out var selectedFolder) && selectedFolder is not null)
            await LaunchAsync(new LaunchIntent(selectedFolder, LaunchMode.Picker));
    }

    private async Task OpenFolderAsync()
    {
        if (_workStatus is not null || TerminalPending)
            return;

        if (!TryGetFolder(out var folder) || folder is null)
            return;

        await LaunchAsync(new LaunchIntent(folder));
    }

    private async Task LaunchAsync(LaunchIntent intent, bool afterRepair = false)
    {
        var manualCheckAtLaunch = _manualCheckCount;
        var folder = intent.Folder;
        LaunchResult launch;
        _notice = null;
        _activeLaunch = intent;
        _status = "TerminalPending";
        Render();
        try
        {
            if (!await Task.Run(() => Directory.Exists(folder)))
            {
                ShowNotice(new Notice("InvalidFolder", folder, intent));
                _status = null;
                return;
            }

            launch = await RunTerminalAsync(intent);
            if (!afterRepair && intent.Mode == LaunchMode.Resume && launch is { Status: "Failed", ExitCode: not null })
            {
                var index = await ReloadSessionsAsync();
                if (_manualCheckCount == manualCheckAtLaunch &&
                    index.FallbackFolders.Contains(folder, StringComparer.OrdinalIgnoreCase) &&
                    index.Sessions.All(session => session.Id != intent.SessionId) &&
                    await Task.Run(() => Directory.Exists(folder)))
                {
                    intent = new LaunchIntent(folder, LaunchMode.Picker);
                    _activeLaunch = intent;
                    _status = "SessionFallbackRunning";
                    Render();
                    launch = await RunTerminalAsync(intent);
                }
            }
        }
        catch (Exception exception)
        {
            Debug.WriteLine(exception.GetType().Name);
            launch = new LaunchResult("StartFailed", null);
        }
        finally
        {
            _activeLaunch = null;
            Render();
        }

        switch (launch.Status)
        {
            case "Exited":
                _status = "TerminalExited";
                Render();
                RememberFolder(folder);
                await Task.WhenAll(ReloadSessionsAsync(), LoadHistoryAsync());
                return;
            case "Canceled":
                _status = "TerminalCanceled";
                Render();
                return;
            case "Unknown":
                _status = "TerminalUnknown";
                Render();
                return;
        }

        if (launch is { Status: "Failed", ExitCode: int exitCode })
        {
            _status = null;
            ShowNotice(new Notice("CliExitedWithError", folder, intent, ExitCode: exitCode));
            return;
        }

        _status = "TerminalFailed";
        var failure = new Notice(afterRepair ? "RetryFailed" : "LaunchFailed", folder, intent, ExitCode: launch.ExitCode);
        if (afterRepair)
        {
            ShowNotice(failure);
            return;
        }

        if (_manualCheckCount != manualCheckAtLaunch)
        {
            Render();
            return;
        }

        if (launch.Status is "CommandMissing" or "StartFailed" || launch is { Status: "Failed", ExitCode: null })
            await CheckAsync(folder, intent, failure);
        else
            ShowNotice(failure);
    }

    private static Task<LaunchResult> RunTerminalAsync(LaunchIntent intent) => intent.Mode switch
    {
        LaunchMode.Resume => TerminalLauncher.ResumeAsync(intent.Folder, intent.SessionId),
        LaunchMode.Picker => TerminalLauncher.PickSessionAsync(intent.Folder),
        _ => TerminalLauncher.RunAsync(intent.Folder)
    };

    private async void Diagnose_Click(object sender, RoutedEventArgs e)
    {
        if (_workStatus is not null || !TryGetFolder(out var folder))
            return;

        _manualCheckCount++;
        if (!TerminalPending)
            _status = null;

        // A standalone setup check uses a stable working directory even before the user picks a project.
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var cwd = folder ?? (Directory.Exists(profile) ? profile : AppContext.BaseDirectory);
        var intent = _notice?.Intent ?? _activeLaunch;
        if (intent is null && folder is not null)
            intent = new LaunchIntent(folder);

        var openAfterLogin = _notice?.Code == "AuthRequired" && intent is not null && !TerminalPending;
        await CheckAsync(cwd, intent, openIfReady: openAfterLogin);
    }

    private async Task CheckAsync(string cwd, LaunchIntent? intent, Notice? launchFailure = null,
        bool openIfReady = false)
    {
        var openFolder = false;
        _notice = null;
        SetWork("Checking");
        try
        {
            var result = await Diagnostics.CheckAsync(cwd);
            openFolder = openIfReady && result.Code == "Ready";
            if (launchFailure is not null && result.Code == "Ready")
                ShowNotice(launchFailure);
            else if (!openFolder)
                ShowNotice(new Notice(result.Code, cwd, intent, result.Detail, AllowRepair: true));
        }
        catch (Exception exception)
        {
            Debug.WriteLine(exception.GetType().Name);
            ShowNotice(new Notice("Unknown", cwd, intent));
        }
        finally
        {
            SetWork(null);
        }

        if (openFolder && intent is not null)
            await LaunchAsync(intent, afterRepair: true);
    }

    private async void Repair_Click(object sender, RoutedEventArgs e)
    {
        if (_workStatus is not null || TerminalPending ||
            _notice is not { } notice || !CanRepair(notice))
        {
            return;
        }

        var startAfterRepair = false;
        _notice = null;
        _status = null;
        SetWork(notice.Code == "Missing" ? "Installing" : "Repairing");
        try
        {
            var result = await Diagnostics.RepairAsync(notice.Cwd, notice.Code, notice.Detail);
            if (result.Code == "Ready")
            {
                SetWork("Verifying");
                result = await Diagnostics.CheckAsync(notice.Cwd);
            }

            startAfterRepair = result.Code == "Ready" && notice.Intent is not null;
            if (!startAfterRepair)
                ShowNotice(new Notice(result.Code, notice.Cwd, notice.Intent, result.Detail, AllowRepair: true));
        }
        catch (Exception exception)
        {
            Debug.WriteLine(exception.GetType().Name);
            ShowNotice(new Notice("RepairFailed", notice.Cwd, notice.Intent));
        }
        finally
        {
            SetWork(null);
        }

        if (startAfterRepair && notice.Intent is { } intent)
            await LaunchAsync(intent, afterRepair: true);
    }

    private void LoginCommand_Click(object sender, RoutedEventArgs e)
        => CopyToClipboard(LoginCommand, "LoginCopied");

    private void CopySummary_Click(object sender, RoutedEventArgs e)
    {
        if (_notice is not { } notice)
            return;

        // The summary is shareable without the selected path or the diagnostic module's raw detail.
        var summary = $"{ResultTitle.Text} [{notice.Code}]{Environment.NewLine}{ResultBody.Text}";
        if (notice.ExitCode is int exitCode)
            summary += $"{Environment.NewLine}{string.Format(CultureInfo.CurrentCulture, UiLanguage.Text("ExitCode"), exitCode)}";

        CopyToClipboard(summary, "SummaryCopied");
    }

    private void CopyToClipboard(string value, string success)
    {
        try
        {
            Clipboard.SetText(value);
            _status = success;
        }
        catch (ExternalException)
        {
            _status = "CopyFailed";
        }

        Render();
    }

    private void Help_Click(object sender, RoutedEventArgs e)
    {
        const string setupHelp = "https://code.claude.com/docs/en/setup#install-on-native-windows";
        var url = _notice switch
        {
            { Code: "AuthRequired" } => "https://code.claude.com/docs/en/authentication",
            { Code: "Missing" } => setupHelp,
            { Code: "RepairFailed", Detail: string detail } when detail.StartsWith("WinGet", StringComparison.Ordinal) => setupHelp,
            _ => "https://code.claude.com/docs/en/troubleshooting"
        };

        try
        {
            using var browser = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            _status = "HelpFailed";
            Render();
        }
    }

    private bool TryGetFolder(out string? folder)
    {
        folder = null;
        var input = FolderInput.Text.Trim();
        if (input.Length == 0)
            return true;

        folder = FullFolderPath(input);
        if (folder is not null)
            return true;

        ShowNotice(new Notice("InvalidFolder", "", new LaunchIntent(input)));
        return false;
    }

    private static string? FullFolderPath(string input)
    {
        if (input.Length > 1 && input[0] == '"' && input[^1] == '"')
            input = input[1..^1];

        return WorkingFolder.Normalize(input);
    }

    private async Task LoadHistoryAsync()
    {
        var loadNumber = ++_historyLoadCount;
        try
        {
            var history = await FolderHistory.LoadAsync();
            if (loadNumber != _historyLoadCount)
                return;

            _folderHistory = history;
            _folderHistoryUnavailable = false;
            RenderRecentFolders();
        }
        catch (Exception exception)
        {
            Debug.WriteLine(exception.GetType().Name);
            if (loadNumber == _historyLoadCount)
            {
                _folderHistoryUnavailable = true;
                RenderRecentFolders();
            }
        }
    }

    private void RenderRecentFolders()
    {
        var fromSessions = (_sessions?.Sessions ?? []).Select(session => new RecentFolder(session.Cwd, session.ModifiedUtc));
        var rows = (_folderHistory ?? []).Concat(fromSessions)
            .OrderByDescending(folder => folder.LastUsedUtc)
            .DistinctBy(folder => folder.Path, StringComparer.OrdinalIgnoreCase)
            .Select(folder => new FolderRow(folder.Path))
            .ToArray();
        RecentItems.ItemsSource = rows;
        RecentList.Visibility = rows.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        HistoryNotice.Visibility = rows.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var notice = _folderHistoryUnavailable ? "HistoryUnavailable"
            : _folderHistory is null || _sessions is null ? "HistoryLoading"
            : "HistoryEmpty";
        HistoryNotice.SetResourceReference(TextBlock.TextProperty, notice);
    }

    private static void RememberFolder(string folder)
    {
        try
        {
            FolderHistory.Remember(folder);
        }
        catch (Exception exception)
        {
            Debug.WriteLine(exception.GetType().Name);
        }
    }

    private async Task<SessionIndex> ReloadSessionsAsync()
    {
        var loadNumber = ++_sessionLoadCount;
        SessionIndex sessionIndex;
        try
        {
            sessionIndex = await SessionHistory.LoadAsync();
        }
        catch (Exception exception)
        {
            Debug.WriteLine(exception.GetType().Name);
            sessionIndex = new SessionIndex(_sessions?.Sessions ?? [], HasReadErrors: true)
            {
                FallbackFolders = _sessions?.FallbackFolders ?? []
            };
        }

        if (loadNumber == _sessionLoadCount)
        {
            _sessions = sessionIndex;
            var fallback = sessionIndex.FallbackFolders.Select(folder => new FolderRow(folder)).ToArray();
            FallbackFolderItems.ItemsSource = fallback;
            FallbackFolderList.Visibility = fallback.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            RenderRecentFolders();
            RenderSessions();
            Render();
        }

        return sessionIndex;
    }

    private void RenderSessions()
    {
        var folder = FullFolderPath(FolderInput.Text.Trim());
        var filter = folder is not null && !_showAllSessions;
        var culture = CultureInfo.GetCultureInfo(UiLanguage.Current);
        var records = _sessions?.Sessions ?? [];
        var rows = records
            .Where(session => !filter || string.Equals(folder, session.Cwd, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(session => session.ModifiedUtc)
            .Select(session => new SessionRow(session, session.ModifiedUtc.ToLocalTime().ToString("g", culture)))
            .ToArray();

        SessionItems.ItemsSource = rows;
        SessionItems.Visibility = rows.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        SessionNotice.Visibility = rows.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        SessionNotice.Text = UiLanguage.Text(_sessions is null ? "SessionsLoading" : filter ? "SessionsEmptyFolder" : "SessionsEmpty");
        SessionScopePanel.Visibility = folder is null ? Visibility.Collapsed : Visibility.Visible;
        SessionScope.Text = UiLanguage.Text(filter ? "SessionScopeFolder" : "SessionScopeAll");
        ShowAllSessionsButton.Content = UiLanguage.Text(filter ? "ShowAllSessions" : "ShowFolderSessions");
    }

    private void ShowNotice(Notice notice)
    {
        _notice = notice;
        Render();
    }

    private void SetWork(string? status)
    {
        _workStatus = status;
        Render();
    }

    private void Render()
    {
        var canChoose = _workStatus is null && !TerminalPending;
        FolderInput.IsEnabled = canChoose;
        BrowseButton.IsEnabled = canChoose;
        RecentItems.IsEnabled = canChoose;
        SessionItems.IsEnabled = canChoose;
        FallbackFolderItems.IsEnabled = canChoose;
        SessionPickerButton.IsEnabled = canChoose;
        OpenButton.IsEnabled = canChoose && !string.IsNullOrWhiteSpace(FolderInput.Text);
        DiagnoseButton.IsEnabled = _workStatus is null;
        DiagnoseLabel.Text = UiLanguage.Text(_notice?.Code switch
        {
            "AuthRequired" when _notice.Intent is not null && !TerminalPending => "CheckLoginAndOpen",
            "AuthRequired" => "CheckAfterLogin",
            not null => "CheckAgain",
            _ => "CheckSetup"
        });
        AutomationProperties.SetName(DiagnoseButton, DiagnoseLabel.Text);

        var status = _workStatus ?? _status;
        StatusStrip.Visibility = status is null ? Visibility.Collapsed : Visibility.Visible;
        StatusText.Text = status is null ? "" : UiLanguage.Text(status);
        ActivityIndicator.Visibility = _workStatus is not null || TerminalPending
            ? Visibility.Visible : Visibility.Collapsed;

        var failedResume = _notice?.Intent?.Mode == LaunchMode.Resume &&
            _notice.Code is "CliExitedWithError" or "LaunchFailed" or "RetryFailed" or "Unknown";
        var showFallback = _sessions?.HasReadErrors == true || failedResume;
        SessionFallbackPanel.Visibility = showFallback ? Visibility.Visible : Visibility.Collapsed;
        SessionFallbackHint.Text = UiLanguage.Text(failedResume ? "SessionResumeUnavailable"
            : _sessions?.FallbackFolders.Count > 0 ? "SessionsPartialWithFolders"
            : "SessionsPartial");

        ResultCard.Visibility = _notice is null ? Visibility.Collapsed : Visibility.Visible;
        if (_notice is not { } notice)
            return;

        var code = notice.Code switch
        {
            "Ready" or "Missing" or "PathMissing" or "StalePath" or "AuthRequired" or "Conflict" or
            "InvalidFolder" or "RepairFailed" or "LaunchFailed" or "RetryFailed" or "CliExitedWithError" => notice.Code,
            _ => "Unknown"
        };
        ResultTitle.Text = UiLanguage.Text(code + "Title");
        ResultBody.Text = UiLanguage.Text(code + "Body");
        ResultFolder.Text = notice.Intent?.Folder ?? "";
        ResultFolderPanel.Visibility = string.IsNullOrWhiteSpace(ResultFolder.Text)
            ? Visibility.Collapsed : Visibility.Visible;
        var intendedAction = notice.Intent?.Mode switch
        {
            LaunchMode.Resume => "ResumeIntent",
            LaunchMode.Picker => "PickerIntent",
            _ => null
        };
        ResultIntent.Visibility = intendedAction is null ? Visibility.Collapsed : Visibility.Visible;
        ResultIntent.Text = intendedAction is null ? "" : UiLanguage.Text(intendedAction);
        ResultDetail.Text = notice.ExitCode is int exitCode
            ? string.Format(CultureInfo.CurrentCulture, UiLanguage.Text("ExitCode"), exitCode)
            : LocalizeDetail(notice.Detail);
        ResultDetailPanel.Visibility = string.IsNullOrWhiteSpace(ResultDetail.Text)
            ? Visibility.Collapsed : Visibility.Visible;

        var showRepair = CanRepair(notice);
        RepairButton.IsEnabled = canChoose;
        RepairButton.Visibility = showRepair ? Visibility.Visible : Visibility.Collapsed;
        if (showRepair)
        {
            var label = code switch
            {
                "Missing" => "Install",
                "StalePath" => "RefreshPath",
                _ => "RepairPath"
            };
            if (notice.Intent is not null)
                label += "AndOpen";
            RepairLabel.Text = UiLanguage.Text(label);
            AutomationProperties.SetName(RepairButton, RepairLabel.Text);
        }

        ResultActions.Visibility = code is "Ready" or "InvalidFolder" ? Visibility.Collapsed : Visibility.Visible;
        LoginCommandButton.Visibility = code == "AuthRequired" ? Visibility.Visible : Visibility.Collapsed;
    }

    private static bool CanRepair(Notice notice) => notice.AllowRepair && (notice.Code == "Missing" ||
        (notice.Code is "PathMissing" or "StalePath" && WorkingFolder.Normalize(notice.Detail) is not null));

    private static string LocalizeDetail(string? detail)
    {
        if (string.IsNullOrEmpty(detail))
            return "";

        // Details can start with a stable evidence identifier, followed by an unchanged path or error code.
        var length = detail.AsSpan().IndexOfAny(' ', '\r', '\n');
        if (length < 0)
            length = detail.Length;

        var localized = UiLanguage.TryText("Evidence" + detail[..length]);
        return localized is null ? detail : localized + detail[length..];
    }

    private enum LaunchMode { New, Resume, Picker }

    private sealed record LaunchIntent(string Folder, LaunchMode Mode = LaunchMode.New, Guid SessionId = default);

    private sealed record Notice(string Code, string Cwd, LaunchIntent? Intent, string? Detail = null,
        bool AllowRepair = false, int? ExitCode = null);

    private sealed record SessionRow(SavedSession Session, string ModifiedLocal)
    {
        public string AccessibleName => $"{Session.Title}, {Session.Cwd}, {ModifiedLocal}";
    }

    private sealed record FolderRow(string Path)
    {
        public string Name
        {
            get
            {
                var name = System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(Path));
                return string.IsNullOrEmpty(name) ? Path : name;
            }
        }
    }
}
