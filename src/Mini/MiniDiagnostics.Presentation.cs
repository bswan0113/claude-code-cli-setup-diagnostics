using System.Globalization;

namespace ClaudeLauncher;

internal sealed partial class MiniDiagnostics
{
    private static string T(string? key) => key is null ? "" : UiLanguage.Text(key);
    private static string Format(string key, params object[] values) =>
        string.Format(CultureInfo.GetCultureInfo(UiLanguage.Current), T(key), values);
    private static string Lines(params string?[] values) => string.Join(Environment.NewLine,
        values.Where(v => !string.IsNullOrWhiteSpace(v)).Distinct(StringComparer.Ordinal));

    private static string LocalizeEvidence(string? detail)
    {
        if (string.IsNullOrEmpty(detail)) return "";
        var length = detail.AsSpan().IndexOfAny(' ', '\r', '\n');
        if (length < 0) length = detail.Length;
        var known = UiLanguage.TryText("Evidence" + detail[..length]);
        return known is null ? detail : known + detail[length..];
    }

    private static MiniState Present(Data s)
    {
        var guide = s.Notice is null ? null : GuidedDiagnosis.Evaluate(s.Notice, s.Checks);
        var found = guide?.Finding is { } item ? new DiagnosticItemView(item, guide.ManualExecutableAdvice) : null;
        var hasAction = guide?.Repair is not null || guide?.QuickRepair is not null;
        var notice = s.Notice;
        var title = found?.Title ?? (notice is null ? "" : UiLanguage.TryText(notice.Code + "Title") ?? T("UnknownTitle"));
        var body = hasAction ? Question(guide!) : found?.Solution ??
            (notice is null ? "" : UiLanguage.TryText(notice.Code + "Body") ?? T("UnknownBody"));
        var integrity = found is not null && GuidedDiagnosis.IsIntegrityFinding(found.Check.Key);
        if (integrity)
            body = Lines(found!.Cause, GuidedDiagnosis.ObservedExecutable(found.Check),
                hasAction ? T("GuidedPreserveExecutable") : null, body);
        else if (found is not null)
            body = Lines(found.Cause, body);
        if (found?.Check.SettingsError is not null) body = Lines(found.Evidence, body);
        if (guide?.Repair?.Action == "InstallClaudeNative" || guide?.QuickRepair?.Code == "Missing")
            body = Lines(body, T("GuidedFallbackNotice"));
        var evidence = LocalizeEvidence(notice?.Detail);
        if (notice?.Code == "RepairFailed" && evidence.Length > 0)
            body = evidence.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? body;
        var target = guide?.QuickRepair?.Code == "Missing" ? Diagnostics.NativeInstallCommand :
            guide?.Repair?.Target ?? guide?.QuickRepair?.Detail ?? "";
        var recommended = !hasAction ? "" : T(guide?.QuickRepair?.Code == "AuthRequired" ? "StartLogin" :
            guide?.Repair?.Action == SettingsDiagnostics.RestoreAction ? "PreviewSettings" : "GuidedYes");
        var defender = integrity ? null : s.Checks.FirstOrDefault(c => c.Key is "DefenderQuarantined" or "DefenderPossible");
        var reviewed = GuidedDiagnosis.ReconcileExecutable(notice, s.Checks);
        var stages = notice is null ? [] : Enumerable.Range(1, 8).Select(stage => new DiagnosticStageView(stage,
            reviewed.Where(check => check.Stage == stage).Select(check => new DiagnosticItemView(check,
                guide?.Repair is null && check == guide?.Finding ? guide?.ManualExecutableAdvice : null)).ToArray(), false)).ToArray();
        return new(guide?.StatusKey ?? "", T(guide?.StatusKey), title, body,
            Lines(found?.Cause, found?.Evidence, evidence), target, recommended, T(s.Work), T(s.Feedback),
            defender is null ? "" : T(defender.Key == "DefenderQuarantined" ? "DefenderConfirmedSummary" : "DefenderPossibleSummary"),
            T(s.HistoryNotice), s.Work is not null, s.CanCancel, stages, s.Entries.Select(HistoryRow).ToArray(), Approval(s.Approval))
            { ScanId = s.ScanId };
    }

    private static string Question(GuidedDiagnosis guide)
    {
        if (guide.Repair is { Action: "RestoreClaudeBackup" } backup)
        {
            var name = Path.GetFileName(backup.Evidence);
            return Version.TryParse(name, out _) ? Format("GuidedRestoreVersion", name) : T("GuidedRestore");
        }
        if (guide.Repair is { } repair)
            return repair.Action switch
            {
                "InstallClaudeNative" => T("GuidedReinstall"),
                "SetUserGitBash" => T("GuidedGitBash"),
                "RefreshClaudeProcessPath" => T("GuidedRefreshPath"),
                "PreferClaudePath" => T("GuidedUserPath"),
                SettingsDiagnostics.RestoreAction => T("GuidedSettingsPreview"),
                _ => new DiagnosticItemView(repair).ActionDescription,
            };
        return T(guide.QuickRepair?.Code switch
        {
            "Missing" => "GuidedInstall",
            "PathMissing" => "GuidedUserPath",
            "StalePath" => "GuidedRefreshPath",
            "AuthRequired" => "GuidedLogin",
            _ => "UnknownBody",
        });
    }

    private static MiniHistoryRow HistoryRow(RepairHistoryEntry entry)
    {
        var title = UiLanguage.TryText("HistoryAction" + entry.Action) ?? UiLanguage.TryText("Fix" + entry.Action) ?? T("RecordedRepair");
        if (entry.IsUndo) title = Format("HistoryUndoAction", title);
        var status = UiLanguage.TryText("History" + entry.Status) ?? T("HistoryUnknown");
        var detail = entry.At == DateTimeOffset.MinValue ? status :
            entry.At.ToLocalTime().ToString("g", CultureInfo.GetCultureInfo(UiLanguage.Current)) + "  ·  " + status;
        if (!string.IsNullOrEmpty(entry.Subject)) detail += "  ·  " + entry.Subject;
        if (!entry.CanUndo && !entry.IsUndo && entry.Status == "Applied") detail += "  ·  " + T("HistoryOnly");
        return new(entry.Id, title, detail, entry.CanUndo);
    }

    private static MiniApproval? Approval(Pending? pending)
    {
        if (pending is null) return null;
        if (pending.Undo is { } undo)
        {
            var row = HistoryRow(undo);
            return new(pending.Id, T("UndoRepair"),
                T(undo.Action == SettingsDiagnostics.RestoreAction ? "UndoSettingsQuestion" : "UndoRepairQuestion"),
                row.Title, row.Detail);
        }
        var item = new DiagnosticItemView(pending.Item!);
        if (pending.Preview is not { } preview)
            return new(pending.Id, item.ActionLabel!, item.ActionDescription, item.Target, item.Evidence);
        var paths = item.Target.Split(Environment.NewLine, StringSplitOptions.None);
        return new(pending.Id, T("FixRestoreSettingsBackup"),
            Lines(T(preview.ScopeKey), preview.KnownSection is { } section ? Format("SettingsChangedSection", section) : null,
                T("SettingsDiffRedacted")),
            Lines(Format("SettingsFileTarget", paths[0]), paths.Length > 1 ? Format("SettingsBackupSource", paths[1]) : null,
                Format("SettingsKeepOriginal", preview.RecoveryPath)), preview.Difference);
    }
}
