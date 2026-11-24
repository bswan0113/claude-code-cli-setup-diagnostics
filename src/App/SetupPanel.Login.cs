using System.Diagnostics;

namespace ClaudeLauncher;

public partial class SetupPanel
{
    private async Task LoginAndVerifyAsync()
    {
        using var cancel = new CancellationTokenSource();
        _loginCancellation = cancel;
        _retryVerified = false;
        DetailsExpander.IsExpanded = false;
        Begin("LoginWaiting");
        DiagnosticResult? status = null;
        bool completed = false;
        try
        {
            completed = await Task.Run(() => RepairJournal.TrackAsync("ClaudeLogin", async () =>
            {
                bool finished;
                try { finished = await RepairInteractions.LoginAsync(_cwd, cancel.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (cancel.IsCancellationRequested) { finished = false; }
                status = await Diagnostics.CheckWithCancellationAsync(_cwd, CancellationToken.None).ConfigureAwait(false);
                return finished && status.Code == "Ready";
            }, authenticated => authenticated));
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex.GetType().Name);
        }
        _loginCancellation = null;
        Begin("FullVerifying");
        try
        {
            await RefreshAsync(CancellationToken.None, checkedStatus: status);
            if (!completed && _notice?.Code != "Ready")
                _feedback = "LoginUnavailable";
            _retryVerified = _intent is not null && GuidedDiagnosis.Evaluate(_notice, _checks).StatusKey == "GuidedHealthy";
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex.GetType().Name);
            InvalidateResults();
            _feedback = "FullVerifyFailed";
        }
        finally { End(); }
        if (_retryVerified && completed && !cancel.IsCancellationRequested && _intent is not null)
            Finished?.Invoke(_intent);
        else if (HistoryExpander.IsExpanded)
            await LoadRepairHistoryAsync();
    }
}
