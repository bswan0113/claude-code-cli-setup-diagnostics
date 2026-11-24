using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace ClaudeLauncher;

public partial class SetupPanel
{
    private bool _retryVerified;
    private CancellationTokenSource? _scanCancellation;
    private IReadOnlyList<CheckItem> _checks = [];
    private RepairApproval? _approval;
    private IInputElement? _reviewFocus;
    private double _reviewScrollOffset;

    private async Task RefreshAsync(CancellationToken token, DiagnosticNotice? launchFailure = null,
        DiagnosticResult? checkedStatus = null)
    {
        var full = FullDiagnostics.ScanAsync(_cwd, token);
        var quick = checkedStatus is null ? Diagnostics.CheckWithCancellationAsync(_cwd, token) : Task.FromResult(checkedStatus);
        await Task.WhenAll(full, quick);
        token.ThrowIfCancellationRequested();
        _checks = await full;
        var result = await quick;
        _notice = result.Code == "Ready" && launchFailure is not null
            ? launchFailure : new DiagnosticNotice(result.Code, result.Detail, AllowRepair: true);
        FullStages.ItemsSource = Enumerable.Range(1, 8).Select(stage => new DiagnosticStageView(stage,
            _checks.Where(check => check.Stage == stage).Select(check => new DiagnosticItemView(check)).ToArray(),
            expanded: false)).ToArray();
    }

    private async void ReviewCheck_Click(object sender, RoutedEventArgs e)
    {
        if (IsBusy || !DetailsExpander.IsExpanded || _approval is not null ||
            sender is not Button { DataContext: DiagnosticItemView item } || !item.CanReview)
        {
            return;
        }

        if (item.Check.Action == SettingsDiagnostics.RestoreAction)
            await ReviewSettingsBackupAsync(item.Check);
        else
            OpenApproval(new RepairApproval(item.ActionLabel!, item.ActionDescription, item.Target, item.Evidence, item.Check));
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
        if (approval.UndoId is Guid id)
            await ApplyAndVerifyAsync(async () => (await RepairJournal.UndoAsync(id, CancellationToken.None), null),
                installing: false, unavailableFeedback: "UndoUnavailable");
        else if (approval.Item is { } item)
            await ApplyFullRepairAsync(item);
    }

    private async Task ApplyFullRepairAsync(CheckItem item)
    {
        await ApplyAndVerifyAsync(async () =>
        {
            var result = await FullDiagnostics.ApplyWithOutcomeAsync(_cwd, item, CancellationToken.None);
            return (result.Applied, result.Failure is { } failure ? new DiagnosticNotice(failure.Code, failure.Detail) : null);
        }, installing: item.Action == "InstallClaudeNative");
    }

    private void RetryIntent_Click(object sender, RoutedEventArgs e)
    {
        if (!IsBusy && _retryVerified && _intent is not null)
        {
            _retryVerified = false;
            Finished?.Invoke(_intent);
        }
    }

    private static Visibility Show(bool show) => show ? Visibility.Visible : Visibility.Collapsed;

    private static string Format(string key, params object[] arguments) =>
        string.Format(CultureInfo.GetCultureInfo(UiLanguage.Current), UiLanguage.Text(key), arguments);

    private sealed record RepairApproval(string Title, string Description, string Target, string Evidence,
        CheckItem? Item, Guid? UndoId = null);
}
