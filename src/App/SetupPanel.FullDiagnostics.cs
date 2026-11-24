using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace ClaudeLauncher;

public partial class SetupPanel
{
    private bool _showFull;
    private bool _retryVerified;
    private CancellationTokenSource? _scanCancellation;
    private IReadOnlyList<DiagnosticStageView> _stages = [];
    private RepairApproval? _approval;
    private IInputElement? _reviewFocus;
    private double _reviewScrollOffset;

    private async void FullScan_Click(object sender, RoutedEventArgs e)
    {
        if (IsBusy || _approval is not null || WorkingFolder.Normalize(_cwd) is null)
            return;

        _showFull = true;
        _retryVerified = false;
        ComparisonText.Visibility = Visibility.Collapsed;
        using var cancellation = new CancellationTokenSource();
        _scanCancellation = cancellation;
        Begin("FullChecking");
        try
        {
            await RefreshFullAsync(cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            _feedback = "FullCheckCanceled";
        }
        catch (Exception exception)
        {
            Debug.WriteLine(exception.GetType().Name);
            _feedback = "FullCheckFailed";
        }
        finally
        {
            _scanCancellation = null;
            End();
            if (_showFull)
                FullHeading.Focus();
        }
    }

    private async Task<IReadOnlyList<CheckItem>> RefreshFullAsync(CancellationToken token, int? revealStage = null)
    {
        var checks = await FullDiagnostics.ScanAsync(_cwd, token);
        token.ThrowIfCancellationRequested();
        var expanded = _stages.Where(stage => stage.Expanded).Select(stage => stage.Stage).ToHashSet();
        _stages = Enumerable.Range(1, 8).Select(stage => new DiagnosticStageView(stage,
            checks.Where(check => check.Stage == stage).Select(check => new DiagnosticItemView(check)).ToArray(),
            expanded.Contains(stage) || revealStage == stage)).ToArray();
        FullStages.ItemsSource = _stages;
        return checks;
    }

    private void ReviewCheck_Click(object sender, RoutedEventArgs e)
    {
        if (IsBusy || !_showFull || _approval is not null ||
            sender is not Button { DataContext: DiagnosticItemView item } || !item.CanReview)
        {
            return;
        }

        OpenApproval(new RepairApproval(item.ActionLabel!, item.Solution, item.Target, item.Evidence, Item: item.Check));
    }

    private void OpenApproval(RepairApproval approval)
    {
        _reviewFocus = Keyboard.FocusedElement;
        _reviewScrollOffset = BodyScroll.VerticalOffset;
        _approval = approval;
        Render();
        BodyScroll.ScrollToTop();
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (ReferenceEquals(_approval, approval) && IsVisible)
                CancelApprovalButton.Focus();
        });
    }

    private void CancelApproval_Click(object sender, RoutedEventArgs e) => CancelApproval();

    private void CancelApproval()
    {
        _approval = null;
        Render();
        var previous = _reviewFocus;
        var offset = _reviewScrollOffset;
        _reviewFocus = null;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            BodyScroll.ScrollToVerticalOffset(offset);
            if (previous is UIElement { IsVisible: true, IsEnabled: true, Focusable: true })
                Keyboard.Focus(previous);
        });
    }

    private async void ConfirmApproval_Click(object sender, RoutedEventArgs e)
    {
        if (IsBusy || _approval is not { } approval)
            return;

        _approval = null;
        _reviewFocus = null;
        if (approval.Item is { } item)
            await ApplyFullRepairAsync(item);
        else if (approval.Quick is { } quick)
            await ApplyQuickRepairAsync(quick);
    }

    private async Task ApplyFullRepairAsync(CheckItem item)
    {
        _retryVerified = false;
        _notice = null;
        var oldMatches = _stages.First(stage => stage.Stage == item.Stage).Items.Count(row => row.Check.Key == item.Key);
        ComparisonText.Visibility = Visibility.Collapsed;
        Begin("FullApplying");
        var applied = false;
        try
        {
            applied = await FullDiagnostics.ApplyAsync(_cwd, item, CancellationToken.None);
        }
        catch (Exception exception)
        {
            Debug.WriteLine(exception.GetType().Name);
        }

        Begin("FullVerifying");
        try
        {
            var updated = await RefreshFullAsync(CancellationToken.None, item.Stage);
            var matching = updated.Where(check => check.Stage == item.Stage && check.Key == item.Key).ToArray();
            var exact = matching.Where(check => string.Equals(check.Target, item.Target, StringComparison.Ordinal)).ToArray();
            var after = exact.Length == 1 ? exact[0] : oldMatches == 1 && matching.Length == 1 ? matching[0] : null;
            ComparisonText.Text = Format("FullComparison", DiagnosticItemView.StateText(item.State),
                after is null ? UiLanguage.Text("FullNoMatchingResult") : DiagnosticItemView.StateText(after.State));
            ComparisonText.Visibility = Visibility.Visible;
            _feedback = applied ? "FullApplied" : "FullNotApplied";
        }
        catch (Exception exception)
        {
            Debug.WriteLine(exception.GetType().Name);
            // Once an action has run, stale rows must not offer another change.
            _stages = [];
            FullStages.ItemsSource = _stages;
            _feedback = "FullVerifyFailed";
        }

        try
        {
            var quick = await Diagnostics.CheckAsync(_cwd);
            _notice = new DiagnosticNotice(quick.Code, quick.Detail, AllowRepair: true);
            _retryVerified = applied && _intent is not null && quick.Code == "Ready";
        }
        catch (Exception exception)
        {
            Debug.WriteLine(exception.GetType().Name);
            _notice = new DiagnosticNotice("Unknown");
        }
        finally
        {
            End();
            FullHeading.Focus();
        }
    }

    private void BackToQuick_Click(object sender, RoutedEventArgs e)
    {
        if (IsBusy && _scanCancellation is null)
            return;

        _showFull = false;
        _feedback = null;
        _scanCancellation?.Cancel();
        Render();
        BodyScroll.ScrollToTop();
        if (!IsBusy)
            FullScanButton.Focus();
    }

    private void CancelFullScan_Click(object sender, RoutedEventArgs e) => _scanCancellation?.Cancel();

    private void RetryIntent_Click(object sender, RoutedEventArgs e)
    {
        if (IsBusy || !_retryVerified || _intent is null)
            return;

        _retryVerified = false;
        Finished?.Invoke(_intent);
    }

    private void RenderDiagnosticsViews()
    {
        var confirming = _approval is not null;
        var quick = !confirming && !_showFull;
        var full = !confirming && _showFull;
        QuickView.Visibility = Show(quick);
        FullView.Visibility = Show(full);
        FullStages.IsEnabled = !IsBusy;
        ApprovalView.Visibility = Show(confirming);
        FullScanButton.Visibility = Show(quick && !IsBusy && WorkingFolder.Normalize(_cwd) is not null);
        BackToQuickButton.Visibility = Show(full && (!IsBusy || _scanCancellation is not null));
        RescanFullButton.Visibility = Show(full && !IsBusy);
        CancelFullScanButton.Visibility = Show(full && _scanCancellation is not null);
        RetryIntentButton.Visibility = Show(full && !IsBusy && _retryVerified);
        CancelButton.Visibility = Show(quick && !IsBusy);
        CancelApprovalButton.Visibility = ApprovalView.Visibility;
        ConfirmApprovalButton.Visibility = ApprovalView.Visibility;
        if (_approval is { } approval)
        {
            ApprovalActionText.Text = approval.Title;
            ApprovalAboutText.Text = approval.Description;
            ApprovalTargetText.Text = approval.Target;
            ApprovalEvidenceText.Text = approval.Evidence;
            ApprovalEvidenceText.Visibility = Show(!string.IsNullOrWhiteSpace(approval.Evidence));
        }
    }

    private static Visibility Show(bool show) => show ? Visibility.Visible : Visibility.Collapsed;

    private static string Format(string key, params object[] arguments) =>
        string.Format(CultureInfo.GetCultureInfo(UiLanguage.Current), UiLanguage.Text(key), arguments);

    private sealed record RepairApproval(string Title, string Description, string Target, string Evidence,
        CheckItem? Item = null, DiagnosticNotice? Quick = null);
}
