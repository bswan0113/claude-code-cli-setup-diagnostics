using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace ClaudeLauncher;

public partial class SetupPanel : UserControl
{
    private const string LoginCommand = "claude auth login";
    private readonly string _cwd;
    private readonly LaunchIntent? _intent;
    private DiagnosticNotice? _notice;
    private string? _work;
    private string? _feedback;

    internal SetupPanel(string cwd, LaunchIntent? intent, DiagnosticNotice? notice)
    {
        _cwd = cwd;
        _intent = intent;
        _notice = notice;
        InitializeComponent();
        Render();
    }

    internal event Action<LaunchIntent?>? Finished;
    internal bool IsBusy => _work is not null;

    internal async Task OpenAsync(bool examine, DiagnosticNotice? launchFailure)
    {
        if (examine)
            await CheckAsync(launchFailure, reopenAfterLogin: false);

        if (IsVisible && !IsKeyboardFocusWithin)
            CloseButton.Focus();
    }

    private async void Diagnose_Click(object sender, RoutedEventArgs e) =>
        await CheckAsync(launchFailure: null, _notice?.Code == "AuthRequired");

    private async Task CheckAsync(DiagnosticNotice? launchFailure, bool reopenAfterLogin)
    {
        if (IsBusy)
            return;

        var open = false;
        Begin("Checking");
        try
        {
            var result = await Diagnostics.CheckAsync(_cwd);
            open = result.Code == "Ready" && reopenAfterLogin && _intent is not null;
            _notice = result.Code == "Ready" && launchFailure is not null
                ? launchFailure : new DiagnosticNotice(result.Code, result.Detail, AllowRepair: true);
        }
        catch (Exception exception)
        {
            Debug.WriteLine(exception.GetType().Name);
            _notice = new DiagnosticNotice("Unknown");
        }
        finally
        {
            End();
        }

        if (open)
            Finished?.Invoke(_intent);
    }

    private void Repair_Click(object sender, RoutedEventArgs e)
    {
        if (IsBusy || _approval is not null || _notice is not { } notice || !CanRepair(notice))
            return;

        var action = LegacyActionLabel(notice.Code);
        var target = notice.Code switch
        {
            "Missing" => UiLanguage.Text("ApprovalWinGetTarget"),
            "StalePath" => Format("ApprovalProcessPathTarget", notice.Detail!),
            _ => Format("ApprovalUserPathTarget", notice.Detail!)
        };
        OpenApproval(new RepairApproval(UiLanguage.Text(action), UiLanguage.Text(notice.Code + "Body"),
            target, "", Quick: notice));
    }

    private async Task ApplyQuickRepairAsync(DiagnosticNotice notice)
    {
        var open = false;
        Begin(notice.Code == "Missing" ? "Installing" : "Repairing");
        try
        {
            var result = await Diagnostics.RepairAsync(_cwd, notice.Code, notice.Detail);
            if (result.Code == "Ready")
            {
                Begin("Verifying");
                result = await Diagnostics.CheckAsync(_cwd);
            }

            open = result.Code == "Ready" && _intent is not null;
            _notice = new DiagnosticNotice(result.Code, result.Detail, AllowRepair: true);
        }
        catch (Exception exception)
        {
            Debug.WriteLine(exception.GetType().Name);
            _notice = new DiagnosticNotice("RepairFailed");
        }
        finally
        {
            End();
        }

        if (open)
            Finished?.Invoke(_intent);
    }

    private void Begin(string work)
    {
        _work = work;
        _feedback = null;
        Render();
        BodyScroll.ScrollToTop();
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (IsBusy && IsVisible)
                ProgressText.Focus();
        });
    }

    private void End()
    {
        _work = null;
        Render();
        if (IsVisible)
            CloseButton.Focus();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (_approval is not null)
        {
            CancelApproval();
            return;
        }
        if (_scanCancellation is { } cancellation)
        {
            cancellation.Cancel();
            return;
        }
        if (!IsBusy)
            Finished?.Invoke(null);
    }

    private void Panel_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
            return;

        e.Handled = true;
        Close_Click(sender, e);
    }

    private void LoginCommand_Click(object sender, RoutedEventArgs e) =>
        CopyToClipboard(LoginCommand, "LoginCopied");

    private void CopySummary_Click(object sender, RoutedEventArgs e)
    {
        if (_notice is not { } notice)
            return;

        var summary = $"{ResultTitle.Text} [{notice.Code}]{Environment.NewLine}{ResultBody.Text}";
        if (notice.ExitCode is int exitCode)
            summary += $"{Environment.NewLine}{FormatExitCode(exitCode)}";
        CopyToClipboard(summary, "SummaryCopied");
    }

    private void CopyToClipboard(string value, string success)
    {
        try
        {
            Clipboard.SetText(value);
            _feedback = success;
        }
        catch (ExternalException)
        {
            _feedback = "CopyFailed";
        }
        Render();
    }

    private void Help_Click(object sender, RoutedEventArgs e)
    {
        const string installHelp = "https://code.claude.com/docs/en/setup#install-on-native-windows";
        var url = _notice switch
        {
            { Code: "AuthRequired" } => "https://code.claude.com/docs/en/authentication",
            { Code: "Missing" } => installHelp,
            { Code: "RepairFailed", Detail: string detail } when detail.StartsWith("WinGet", StringComparison.Ordinal) => installHelp,
            _ => "https://code.claude.com/docs/en/troubleshooting"
        };

        try
        {
            using var browser = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            _feedback = "HelpFailed";
            Render();
        }
    }

    private void Render()
    {
        CloseButton.IsEnabled = !IsBusy || _scanCancellation is not null;
        ProgressPanel.Visibility = IsBusy ? Visibility.Visible : Visibility.Collapsed;
        ProgressText.Text = _work is null ? "" : UiLanguage.Text(_work);
        FeedbackText.Visibility = _feedback is null ? Visibility.Collapsed : Visibility.Visible;
        FeedbackText.Text = _feedback is null ? "" : UiLanguage.Text(_feedback);
        ResultPanel.Visibility = _notice is null ? Visibility.Collapsed : Visibility.Visible;
        DiagnoseButton.Visibility = Visibility.Collapsed;
        RepairButton.Visibility = Visibility.Collapsed;
        SecondaryActions.Visibility = Visibility.Collapsed;
        RenderDiagnosticsViews();
        if (QuickView.Visibility != Visibility.Visible || _notice is not { } notice)
            return;

        var code = notice.Code switch
        {
            "Ready" or "Missing" or "PathMissing" or "StalePath" or "AuthRequired" or "Conflict" or
            "InvalidFolder" or "RepairFailed" or "LaunchFailed" or "RetryFailed" or "CliExitedWithError" => notice.Code,
            _ => "Unknown"
        };
        ResultTitle.Text = UiLanguage.Text(code + "Title");
        ResultBody.Text = UiLanguage.Text(code + "Body");
        var repair = CanRepair(notice);
        var action = LegacyActionLabel(code);
        ResultDetailLabel.Text = UiLanguage.Text(repair && code != "Missing" ? action : "ResultDetailLabel");
        ResultDetail.Text = notice.ExitCode is int exitCode ? FormatExitCode(exitCode) : LocalizeDetail(notice.Detail);
        ResultDetailPanel.Visibility = string.IsNullOrWhiteSpace(ResultDetail.Text) ? Visibility.Collapsed : Visibility.Visible;

        ResultFolder.Text = _intent?.Folder ?? "";
        ResultFolderPanel.Visibility = _intent is null ? Visibility.Collapsed : Visibility.Visible;
        ResultIntent.Text = _intent?.Mode switch
        {
            LaunchMode.Resume => UiLanguage.Text("ResumeIntent"),
            LaunchMode.Picker => UiLanguage.Text("PickerIntent"),
            _ => ""
        };
        ResultIntent.Visibility = ResultIntent.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (IsBusy)
            return;

        FullScanButton.Style = (Style)FindResource(repair ? "Action" : "PrimaryAction");
        RepairButton.Visibility = repair ? Visibility.Visible : Visibility.Collapsed;
        var repairAction = repair && _intent is not null ? action + "AndOpen" : action;
        RepairLabel.Text = UiLanguage.Text(repairAction);
        AutomationProperties.SetName(RepairButton, RepairLabel.Text);
        DiagnoseButton.Visibility = code is "Ready" or "InvalidFolder" || repair ? Visibility.Collapsed : Visibility.Visible;
        DiagnoseLabel.Text = UiLanguage.Text(code == "AuthRequired"
            ? _intent is not null ? "CheckLoginAndOpen" : "CheckAfterLogin"
            : "CheckAgain");
        AutomationProperties.SetName(DiagnoseButton, DiagnoseLabel.Text);
        SecondaryActions.Visibility = code is "Ready" or "InvalidFolder" ? Visibility.Collapsed : Visibility.Visible;
        LoginCommandButton.Visibility = code == "AuthRequired" ? Visibility.Visible : Visibility.Collapsed;
    }

    private static bool CanRepair(DiagnosticNotice notice) => notice.AllowRepair && (notice.Code == "Missing" ||
        (notice.Code is "PathMissing" or "StalePath" && WorkingFolder.Normalize(notice.Detail) is not null));

    private static string LegacyActionLabel(string code) => code switch
    {
        "Missing" => "Install",
        "StalePath" => "RefreshPath",
        _ => "RepairPath"
    };

    private static string FormatExitCode(int code) =>
        string.Format(CultureInfo.CurrentCulture, UiLanguage.Text("ExitCode"), code);

    private static string LocalizeDetail(string? detail)
    {
        if (string.IsNullOrEmpty(detail))
            return "";

        var length = detail.AsSpan().IndexOfAny(' ', '\r', '\n');
        if (length < 0)
            length = detail.Length;

        var localized = UiLanguage.TryText("Evidence" + detail[..length]);
        return localized is null ? detail : localized + detail[length..];
    }
}
