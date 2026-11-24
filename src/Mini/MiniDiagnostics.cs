using System.ComponentModel;
using System.Diagnostics;

namespace ClaudeLauncher;

internal sealed record MiniApproval(Guid Id, string Title, string Description, string Target, string Evidence);
internal sealed record MiniHistoryRow(Guid Id, string Title, string Detail, bool CanUndo);
internal sealed record MiniState(string StatusKey, string Status, string Title, string Body, string Evidence,
    string Target, string RecommendedLabel, string Work, string Feedback, string Defender, string HistoryNotice,
    bool Busy, bool CanCancel, IReadOnlyList<DiagnosticStageView> Stages, IReadOnlyList<MiniHistoryRow> History,
    MiniApproval? Approval)
{
    internal Guid ScanId { get; init; }
}

internal sealed partial class MiniDiagnostics(string cwd)
{
    private sealed record Pending(Guid Id, CheckItem? Item = null, RepairHistoryEntry? Undo = null,
        SettingsDiagnostics.Preview? Preview = null);
    private sealed record Data
    {
        internal Guid ScanId { get; init; }
        internal DiagnosticNotice? Notice { get; init; }
        internal IReadOnlyList<CheckItem> Checks { get; init; } = [];
        internal IReadOnlyList<RepairHistoryEntry> Entries { get; init; } = [];
        internal string? HistoryNotice { get; init; }
        internal bool HistoryLoaded { get; init; }
        internal Pending? Approval { get; init; }
        internal string? Work { get; init; }
        internal string? Feedback { get; init; }
        internal bool CanCancel { get; init; }
    }

    private readonly object _gate = new();
    private Data _data = new();
    private CancellationTokenSource? _cancellation;
    private bool _closing;
    internal event Action? Changed;
    private Data Snapshot { get { lock (_gate) return _data; } }
    internal MiniState State => Present(Snapshot);

    private void Publish(Func<Data, Data> change)
    {
        lock (_gate) _data = change(_data);
        Changed?.Invoke();
    }

    // Claim the command before scheduling any work. A late click cannot consume a superseded result or approval.
    private Task ExecuteAsync(Data expected, string work, bool canCancel, Func<CancellationToken, Task> command,
        string failureKey)
    {
        CancellationTokenSource cancel;
        lock (_gate)
        {
            if (_closing || _data.Work is not null || !ReferenceEquals(_data, expected)) return Task.CompletedTask;
            _cancellation = cancel = new();
            _data = _data with { Work = work, Feedback = null, Approval = null, CanCancel = canCancel };
        }
        Changed?.Invoke();
        return Task.Run(async () =>
        {
            try { await command(cancel.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
            catch (Exception e)
            {
                Debug.WriteLine(e.GetType().Name);
                Publish(s => failureKey is "FullCheckFailed" or "FullVerifyFailed"
                    ? s with { Notice = new("Unknown"), Checks = [], ScanId = Guid.NewGuid(), Feedback = failureKey }
                    : s with { Feedback = failureKey });
            }
            finally
            {
                lock (_gate)
                {
                    _cancellation = null;
                    _data = _data with { Work = null, CanCancel = false };
                }
                cancel.Dispose();
                Changed?.Invoke();
            }
        });
    }

    internal void CancelReadOrLogin()
    {
        lock (_gate)
            if (_data.CanCancel) _cancellation?.Cancel();
    }

    internal bool RequestClose()
    {
        lock (_gate)
        {
            _closing = true;
            if (_data.Work is null) return true;
            if (_data.CanCancel) _cancellation?.Cancel();
            return false;
        }
    }

    internal Task ScanAsync()
    {
        var s = Snapshot;
        return s.Approval is not null ? Task.CompletedTask :
            ExecuteAsync(s, "FullChecking", true, token => RefreshAsync(token), "FullCheckFailed");
    }

    private async Task RefreshAsync(CancellationToken token, DiagnosticResult? verified = null)
    {
        var full = FullDiagnostics.ScanAsync(cwd, token);
        var quick = verified is null ? Diagnostics.CheckWithCancellationAsync(cwd, token) : Task.FromResult(verified);
        await Task.WhenAll(full, quick).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        var result = await quick.ConfigureAwait(false);
        var checks = await full.ConfigureAwait(false);
        Publish(s => s with { Checks = checks, Notice = new(result.Code, result.Detail, AllowRepair: true),
            ScanId = Guid.NewGuid(), Approval = null });
    }

    internal Task RecommendAsync() => RecommendAsync(Snapshot.ScanId);
    internal Task RecommendAsync(Guid scanId)
    {
        var s = Snapshot;
        if (s.Notice is null || s.ScanId != scanId || s.Approval is not null) return Task.CompletedTask;
        var guidance = GuidedDiagnosis.Evaluate(s.Notice, s.Checks);
        if (guidance.Repair is { } check)
            return check.Action == SettingsDiagnostics.RestoreAction ? PrepareSettingsAsync(s, check) : ApplyFullAsync(s, check);
        if (guidance.QuickRepair is not { } quick) return Task.CompletedTask;
        if (quick.Code == "AuthRequired")
            return ExecuteAsync(s, "LoginWaiting", true, LoginAsync, "FullVerifyFailed");
        return ApplyAsync(s, quick.Code == "Missing", async () =>
        {
            var result = await Diagnostics.RepairAsync(cwd, quick.Code, quick.Detail).ConfigureAwait(false);
            return (null, result.Code == "RepairFailed" ? new DiagnosticNotice(result.Code, result.Detail) : null);
        });
    }

    internal Task ReviewAsync(CheckItem item)
    {
        var s = Snapshot;
        if (s.Notice is null || s.Approval is not null || item.Action is null || item.Revision is null ||
            !GuidedDiagnosis.ReconcileExecutable(s.Notice, s.Checks).Contains(item) ||
            !new DiagnosticItemView(item).CanReview) return Task.CompletedTask;
        if (item.Action == SettingsDiagnostics.RestoreAction) return PrepareSettingsAsync(s, item);
        Offer(s, new(Guid.NewGuid(), Item: item));
        return Task.CompletedTask;
    }

    private Task PrepareSettingsAsync(Data s, CheckItem check) =>
        ExecuteAsync(s, "SettingsPreparingPreview", true, async token =>
        {
            var preview = await SettingsDiagnostics.PreviewAsync(check, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (preview is not null)
                Publish(current => current with { Approval = new(Guid.NewGuid(), Item: check, Preview: preview) });
            else
            {
                await RefreshAsync(token).ConfigureAwait(false);
                Publish(current => current with { Feedback = "SettingsPreviewChanged" });
            }
        }, "SettingsPreviewChanged");

    private void Offer(Data expected, Pending pending)
    {
        lock (_gate)
        {
            if (_closing || _data.Work is not null || !ReferenceEquals(_data, expected)) return;
            _data = _data with { Approval = pending, Feedback = null };
        }
        Changed?.Invoke();
    }

    internal void Decline()
    {
        lock (_gate)
        {
            if (_closing || _data.Work is not null || _data.Approval is null) return;
            _data = _data with { Approval = null };
        }
        Changed?.Invoke();
    }

    internal Task ConfirmAsync(Guid approvalId)
    {
        var s = Snapshot;
        if (s.Approval is not { } approval || approval.Id != approvalId) return Task.CompletedTask;
        if (approval.Undo is { } undo)
            return ApplyAsync(s, false, async () =>
                (await RepairJournal.UndoAsync(undo.Id, CancellationToken.None).ConfigureAwait(false), null), "UndoUnavailable");
        return approval.Item is { } check ? ApplyFullAsync(s, check) : Task.CompletedTask;
    }

    private Task ApplyFullAsync(Data s, CheckItem check) => ApplyAsync(s, check.Action == "InstallClaudeNative", async () =>
    {
        var result = await FullDiagnostics.ApplyWithOutcomeAsync(cwd, check, CancellationToken.None).ConfigureAwait(false);
        return (result.Applied, result.Failure is { } failure ? new DiagnosticNotice(failure.Code, failure.Detail) : null);
    });

    private Task ApplyAsync(Data s, bool install, Func<Task<(bool? Applied, DiagnosticNotice? Failure)>> apply,
        string unavailable = "FullNotApplied") => ExecuteAsync(s, install ? "GuidedInstalling" : "FullApplying", false,
        async _ =>
        {
            (bool? Applied, DiagnosticNotice? Failure) outcome = (false, null);
            try { outcome = await apply().ConfigureAwait(false); }
            catch (Exception e) { Debug.WriteLine(e.GetType().Name); }
            Publish(current => current with { Work = "FullVerifying" });
            await RefreshAsync(CancellationToken.None).ConfigureAwait(false);
            Publish(current => current with { Notice = outcome.Failure ?? current.Notice,
                Feedback = outcome.Applied switch { true => "FullApplied", false => unavailable, null => null } });
            if (s.HistoryLoaded) ReadHistory();
        }, "FullVerifyFailed");

    private async Task LoginAsync(CancellationToken token)
    {
        DiagnosticResult? status = null;
        var completed = false;
        try
        {
            completed = await RepairJournal.TrackAsync("ClaudeLogin", async () =>
            {
                bool ran;
                try { ran = await RepairInteractions.LoginAsync(cwd, token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { ran = false; }
                status = await Diagnostics.CheckWithCancellationAsync(cwd, CancellationToken.None).ConfigureAwait(false);
                return ran && status.Code == "Ready";
            }, ok => ok).ConfigureAwait(false);
        }
        catch (Exception e) { Debug.WriteLine(e.GetType().Name); }
        lock (_gate) if (_closing) return;
        Publish(s => s with { Work = "FullVerifying", CanCancel = false });
        await RefreshAsync(CancellationToken.None, status).ConfigureAwait(false);
        if (!completed && Snapshot.Notice?.Code != "Ready") Publish(s => s with { Feedback = "LoginUnavailable" });
        if (Snapshot.HistoryLoaded) ReadHistory();
    }

    internal Task LoadHistoryAsync()
    {
        var s = Snapshot;
        return s.Approval is null ? ExecuteAsync(s, "HistoryReading", false, _ =>
        {
            ReadHistory();
            return Task.CompletedTask;
        }, "HistoryReadFailed") : Task.CompletedTask;
    }

    private void ReadHistory()
    {
        try
        {
            var entries = RepairJournal.List().OrderByDescending(entry => entry.At).ToArray();
            Publish(s => s with { Entries = entries, HistoryLoaded = true,
                HistoryNotice = entries.Length == 0 ? "HistoryEmptyList" : null });
        }
        catch (Exception e)
        {
            Debug.WriteLine(e.GetType().Name);
            Publish(s => s with { Entries = [], HistoryLoaded = true, HistoryNotice = "HistoryReadFailed" });
        }
    }

    internal Task ReviewUndoAsync(Guid id)
    {
        var s = Snapshot;
        if (s.Approval is null && s.Entries.FirstOrDefault(e => e.Id == id && e.CanUndo) is { } entry)
            Offer(s, new(Guid.NewGuid(), Undo: entry));
        return Task.CompletedTask;
    }

    internal void OpenProtectionHistory()
    {
        if (State.Defender.Length == 0) return;
        if (!DefenderDiagnostics.OpenProtectionHistory()) Publish(s => s with { Feedback = "ProtectionHistoryFailed" });
    }

    internal void OpenHelp()
    {
        var n = Snapshot.Notice;
        string url = n?.Code == "AuthRequired" ? "https://code.claude.com/docs/en/authentication" :
            n?.Code == "Missing" || n is { Code: "RepairFailed", Detail: { } d } &&
                (d.StartsWith("WinGet", StringComparison.Ordinal) || d.StartsWith("NativeInstaller", StringComparison.Ordinal))
                ? Diagnostics.OfficialInstallHelp : "https://code.claude.com/docs/en/troubleshooting";
        try { using var browser = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        { Publish(s => s with { Feedback = "HelpFailed" }); }
    }

    internal string SummaryText()
    {
        var s = Snapshot;
        if (s.Notice is null) return "";
        var view = Present(s);
        return $"{view.Title} [{s.Notice.Code}]{Environment.NewLine}{view.Body}";
    }

    internal void ReportCopy(bool success) => Publish(s => s with { Feedback = success ? "SummaryCopied" : "CopyFailed" });
}
