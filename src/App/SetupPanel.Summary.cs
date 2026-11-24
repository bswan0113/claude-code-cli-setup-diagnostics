using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace ClaudeLauncher;

public partial class SetupPanel
{
    private GuidedDiagnosis? _guided;

    private void RenderSummary()
    {
        var approval = _approval;
        var showResult = !IsBusy && approval is null && _notice is not null;
        QuickView.Visibility = Show(showResult);
        ApprovalView.Visibility = Show(approval is not null);
        ConfirmApprovalButton.Visibility = CancelApprovalButton.Visibility = ApprovalView.Visibility;
        RecommendedAction.Visibility = DiagnoseButton.Visibility = RetryIntentButton.Visibility = Visibility.Collapsed;
        CancelButton.Visibility = Show(showResult);
        _guided = null;
        if (approval is not null)
        {
            ApprovalActionText.Text = approval.Title;
            ApprovalAboutText.Text = approval.Description;
            ApprovalTargetText.Text = approval.Target;
            ApprovalEvidenceText.Text = approval.Evidence;
            ApprovalEvidenceText.Visibility = Show(!string.IsNullOrEmpty(approval.Evidence));
        }
        if (!showResult || _notice is not { } notice)
            return;

        var result = GuidedDiagnosis.Evaluate(notice, _checks);
        _guided = result;
        var finding = result.Finding is { } check ? new DiagnosticItemView(check) : null;
        var hasAction = result.Repair is not null || result.QuickRepair is not null;
        var ink = result.StatusKey switch
        {
            "GuidedHealthy" => "WorkingInk",
            "GuidedInstallNeeded" => "ApprovalInk",
            _ => "IssueInk"
        };
        var fill = ink switch
        {
            "WorkingInk" => "WorkingFill",
            "ApprovalInk" => "ApprovalFill",
            _ => "IssueFill"
        };
        StatusCard.SetResourceReference(Border.BackgroundProperty, fill);
        StatusCard.SetResourceReference(Border.BorderBrushProperty, ink);
        GuidedStatus.SetResourceReference(TextBlock.ForegroundProperty, ink);
        GuidedStatus.Text = UiLanguage.Text(result.StatusKey);

        ResultTitle.Text = finding?.Title ?? UiLanguage.TryText(notice.Code + "Title") ?? UiLanguage.Text("UnknownTitle");
        ResultBody.Text = hasAction ? RepairQuestion(result)
            : finding?.Solution ?? UiLanguage.TryText(notice.Code + "Body") ?? UiLanguage.Text("UnknownBody");
        if (result.Finding?.SettingsError is not null)
            ResultBody.Text = finding!.Evidence + Environment.NewLine + ResultBody.Text;
        if (result.Repair?.Action == "InstallClaudeNative" || result.QuickRepair?.Code == "Missing")
            ResultBody.Text += Environment.NewLine + UiLanguage.Text("GuidedFallbackNotice");
        if (notice.Code == "RepairFailed" && !string.IsNullOrEmpty(notice.Detail))
        {
            // Keep the user-facing reason above the fold; commands and raw evidence remain in details.
            ResultBody.Text = LocalizeDetail(notice.Detail).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()
                ?? ResultBody.Text;
        }
        ResultFolder.Text = _intent?.Folder ?? "";
        ResultFolder.Visibility = Show(ResultFolder.Text.Length > 0);
        ResultDetail.Text = string.Join(Environment.NewLine, new[] { finding?.Cause, finding?.Evidence, LocalizeDetail(notice.Detail),
            notice.ExitCode is int code ? FormatExitCode(code) : null }.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct());
        ResultDetail.Visibility = Show(ResultDetail.Text.Length > 0);
        var target = result.Repair?.Target ?? result.QuickRepair?.Detail;
        if (result.QuickRepair?.Code == "Missing")
            target = Diagnostics.NativeInstallCommand;
        ScopeText.Text = string.IsNullOrWhiteSpace(target) ? "" : UiLanguage.Text("FullChangeTarget") + Environment.NewLine + target;
        ScopeText.Visibility = Show(ScopeText.Text.Length > 0);
        var defender = _checks.FirstOrDefault(check => check.Key is "DefenderQuarantined" or "DefenderPossible");
        DefenderPanel.Visibility = Show(defender is not null && result.StatusKey != "GuidedInstallNeeded");
        DefenderDetailButton.Visibility = Show(defender is not null && DefenderPanel.Visibility != Visibility.Visible);
        if (defender is not null)
            DefenderText.Text = UiLanguage.Text(defender.Key == "DefenderQuarantined" ? "DefenderConfirmedSummary" : "DefenderPossibleSummary");
        RecommendedAction.Content = UiLanguage.Text(result.QuickRepair?.Code == "AuthRequired" ? "StartLogin" :
            result.Repair?.Action == SettingsDiagnostics.RestoreAction ? "PreviewSettings" : "GuidedYes");
        DetailsExpander.Visibility = Show(notice.Code != "InvalidFolder");
        CancelButton.Content = UiLanguage.Text(hasAction ? "GuidedNo" : "CloseSetup");
        RecommendedAction.Visibility = Show(hasAction);
        AutomationProperties.SetHelpText(RecommendedAction, ResultBody.Text);
        RetryIntentButton.Visibility = Show(_retryVerified && !hasAction);
        DiagnoseButton.Visibility = Show(!hasAction && !_retryVerified && result.StatusKey != "GuidedHealthy"
            && notice.Code != "InvalidFolder");
    }

    private static string RepairQuestion(GuidedDiagnosis result)
    {
        if (result.Repair is { } repair)
        {
            if (repair.Action == "RestoreClaudeBackup")
            {
                var file = Path.GetFileName(repair.Evidence);
                return Version.TryParse(file, out _) ? Format("GuidedRestoreVersion", file) : UiLanguage.Text("GuidedRestore");
            }

            return UiLanguage.Text(repair.Action switch
            {
                "InstallClaudeNative" => "GuidedReinstall",
                "SetUserGitBash" => "GuidedGitBash",
                "RefreshClaudeProcessPath" => "GuidedRefreshPath",
                "PreferClaudePath" => "GuidedUserPath",
                SettingsDiagnostics.RestoreAction => "GuidedSettingsPreview",
                _ => throw new InvalidOperationException("Unknown guided repair.")
            });
        }

        return UiLanguage.Text(result.QuickRepair?.Code switch
        {
            "Missing" => "GuidedInstall",
            "PathMissing" => "GuidedUserPath",
            "StalePath" => "GuidedRefreshPath",
            "AuthRequired" => "GuidedLogin",
            _ => throw new InvalidOperationException("Unknown guided quick repair.")
        });
    }
}
