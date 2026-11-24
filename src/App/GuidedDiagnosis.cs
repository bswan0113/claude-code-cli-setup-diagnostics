namespace ClaudeLauncher;

internal sealed record GuidedDiagnosis(string StatusKey, CheckItem? Finding = null,
    CheckItem? Repair = null, DiagnosticNotice? QuickRepair = null)
{
    private const string Issue = "GuidedIssue";
    private const string Install = "GuidedInstallNeeded";

    internal static GuidedDiagnosis Evaluate(DiagnosticNotice? quick, IReadOnlyList<CheckItem> checks)
    {
        if (quick?.Code is "InvalidFolder" or "RepairFailed")
            return new(Issue);
        if (checks.FirstOrDefault(item => item.Key == "Windows32") is { } unsupported)
            return new(Issue, unsupported);

        var binary = checks.FirstOrDefault(item => item.Key is "InstallBinaryMissing" or "InstallBinaryDamaged");
        if (binary is not null)
            return ForBinary(quick, checks, binary);

        if (checks.FirstOrDefault(item => item.Key == "InstallBinaryUnknown") is { } unreadable)
            return new(Issue, unreadable);

        var path = checks.FirstOrDefault(item => item.Stage is 3 or 4 && Eligible(item) &&
            item.Action is "PreferClaudePath" or "RefreshClaudeProcessPath");
        if (path is not null)
            return new(Issue, path, path);

        if (quick is { AllowRepair: true, Code: "StalePath" or "PathMissing" } &&
            WorkingFolder.Normalize(quick.Detail) is not null)
            return new(Issue, QuickRepair: quick);

        if (quick?.Code is "AuthRequired" or "CliExitedWithError" or "RetryFailed" or "LaunchFailed")
            return new(Issue);

        var finding = checks.FirstOrDefault(item => item.State == CheckState.Failed)
            ?? checks.FirstOrDefault(IsProblemWarning);
        if (finding is not null)
        {
            var repair = Eligible(finding) && finding.Action == "SetUserGitBash" ? finding : null;
            return new(Issue, finding, repair);
        }

        var uncertain = checks.FirstOrDefault(item => item.State == CheckState.Unknown);
        if (uncertain is not null)
            return new(Issue, uncertain);

        // A canceled or incomplete scan cannot support a clean bill of health.
        if (!Enumerable.Range(1, 8).All(stage => checks.Any(item => item.Stage == stage)))
            return new(Issue, new CheckItem(1, "InspectionError", CheckState.Unknown));

        // If a verified executable and the quick check disagree, never install over it.
        if (quick?.Code == "Missing")
            return new(Issue, new CheckItem(2, "InstallBinaryUnknown", CheckState.Unknown));
        return new(quick?.Code == "Ready" ? "GuidedHealthy" : Issue);
    }

    private static GuidedDiagnosis ForBinary(DiagnosticNotice? quick, IReadOnlyList<CheckItem> checks, CheckItem binary)
    {
        // A scan and a live launch can straddle an external install or update.
        if (binary.Key == "InstallBinaryMissing" && quick?.Code is "Ready" or "AuthRequired")
            return new(Issue, new CheckItem(2, "InstallBinaryUnknown", CheckState.Unknown, binary.Evidence));

        // InstallChecks orders signed candidates by updater suffix, then modification time and path.
        var backup = checks.FirstOrDefault(item => item.Action == "RestoreClaudeBackup" && Eligible(item));
        if (backup is not null)
            return new(Issue, binary, backup);

        var fresh = binary.Key == "InstallBinaryMissing" && quick?.Code == "Missing" &&
            !checks.Any(item => item.Key is "InstallBackupAvailable" or "InstallBackupUnverified" or
                "InstallNpmOnly" or "InstallLegacy" or "InstallDebris");
        var status = fresh ? Install : Issue;
        var installer = checks.FirstOrDefault(item => item.Action == "InstallClaudeNative" && Eligible(item));
        if (installer is not null)
        {
            // A first install uses the non-forcing fallback if the native installer stalls.
            if (fresh)
                return new(status, binary, QuickRepair: quick!.AllowRepair ? quick : null);
            return new(status, binary, installer);
        }

        // Do not offer to terminate sessions as the default repair. Explain what blocks progress.
        var blocker = checks.FirstOrDefault(item => item.Key is "InstallProcessUnknown" or "InstallProcessOtherSession")
            ?? checks.FirstOrDefault(item => item.Key == "InstallProcessActive");
        return new(status, blocker ?? binary);
    }

    private static bool Eligible(CheckItem item) =>
        item.Action is not null && !string.IsNullOrWhiteSpace(item.Target) && !string.IsNullOrWhiteSpace(item.Revision);

    // A cache, an intentional API key, or a second command on PATH can coexist with a working CLI.
    // Keep those optional checks in the details instead of proposing changes to a healthy installation.
    private static bool IsProblemWarning(CheckItem item) => item.State == CheckState.Warning &&
        (item.Stage == 7 || item.Key is "InstallPathMissing" or "InstallPathStale" or "InstallPathShadowed" or
            "InstallDesktopAlias" or "PolicyNpmBlocked" or "PolicyNpmManual" or "EnvCaMissing" or
            "EnvCaMissingManual" or "EnvGitBashInvalid" or "EnvGitBashInvalidManual");
}
