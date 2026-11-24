using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
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
        if (examine || (launchFailure is not null && launchFailure.Code != "InvalidFolder" && Directory.Exists(_cwd)))
            await CheckAsync(launchFailure, reopenAfterLogin: false);

        if (IsVisible && !IsKeyboardFocusWithin)
            CloseButton.Focus();
    }

    private async void Diagnose_Click(object sender, RoutedEventArgs e) =>
        await CheckAsync(null, _notice?.Code == "AuthRequired");

    private async Task CheckAsync(DiagnosticNotice? launchFailure, bool reopenAfterLogin)
    {
        if (IsBusy)
            return;

        using var cancellation = new CancellationTokenSource();
        _scanCancellation = cancellation;
        _retryVerified = false;
        DetailsExpander.IsExpanded = false;
        Begin("FullChecking");
        try
        {
            await RefreshAsync(cancellation.Token, launchFailure);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            Debug.WriteLine(exception.GetType().Name);
            InvalidateResults();
            _feedback = "FullCheckFailed";
        }
        finally
        {
            _scanCancellation = null;
            End();
        }

        if (reopenAfterLogin && _guided?.StatusKey == "GuidedHealthy" && _intent is not null)
            Finished?.Invoke(_intent);
    }

    private async void Recommended_Click(object sender, RoutedEventArgs e)
    {
        if (IsBusy || _approval is not null || _guided is not { } result)
            return;
        if (result.Repair is { } item)
            await ApplyFullRepairAsync(item);
        else if (result.QuickRepair is { } quick)
            await ApplyQuickRepairAsync(quick);
    }

    private Task ApplyQuickRepairAsync(DiagnosticNotice notice) =>
        ApplyAndVerifyAsync(async () =>
        {
            var result = await Diagnostics.RepairAsync(_cwd, notice.Code, notice.Detail);
            var failed = result.Code == "RepairFailed";
            // RepairAsync also returns a fresh finding when the approved target has changed.
            // Its return value alone cannot attest that a change was made.
            return ((bool?)null,
                failed ? new DiagnosticNotice(result.Code, result.Detail) : null);
        }, installing: notice.Code == "Missing");

    private async Task ApplyAndVerifyAsync(Func<Task<(bool? Applied, DiagnosticNotice? Failure)>> change, bool installing)
    {
        _retryVerified = false;
        DetailsExpander.IsExpanded = false;
        Begin(installing ? "GuidedInstalling" : "FullApplying");
        bool? applied = false;
        DiagnosticNotice? failure = null;
        try
        {
            (applied, failure) = await change();
        }
        catch (Exception exception)
        {
            Debug.WriteLine(exception.GetType().Name);
        }

        Begin("FullVerifying");
        try
        {
            await RefreshAsync(CancellationToken.None);
            _feedback = applied switch { true => "FullApplied", false => "FullNotApplied", _ => null };
            if (failure is not null)
                _notice = failure;
            _retryVerified = applied != false && _intent is not null &&
                GuidedDiagnosis.Evaluate(_notice, _checks).StatusKey == "GuidedHealthy";
        }
        catch (Exception exception)
        {
            Debug.WriteLine(exception.GetType().Name);
            InvalidateResults();
            _feedback = "FullVerifyFailed";
        }
        finally
        {
            End();
        }
    }

    private void InvalidateResults()
    {
        _checks = [];
        FullStages.ItemsSource = null;
        _notice = new DiagnosticNotice("Unknown");
        _retryVerified = false;
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
            Finished?.Invoke(null);
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
        var url = _notice switch
        {
            { Code: "AuthRequired" } => "https://code.claude.com/docs/en/authentication",
            { Code: "Missing" } => Diagnostics.OfficialInstallHelp,
            { Code: "RepairFailed", Detail: string detail } when detail.StartsWith("WinGet", StringComparison.Ordinal) ||
                detail.StartsWith("NativeInstaller", StringComparison.Ordinal) => Diagnostics.OfficialInstallHelp,
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
        RenderSummary();
    }

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
