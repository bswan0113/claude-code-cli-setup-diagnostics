using System.Diagnostics;

namespace ClaudeLauncher;

public partial class MainWindow
{
    private CancellationTokenSource? _profileLoginCancellation;

    private async Task SignInToCurrentProfileAsync()
    {
        // Auth status and interactive sign-in use the same profile-bound child environment as terminals.
        var cwd = Environment.SystemDirectory;
        using var cancellation = new CancellationTokenSource();
        _profileLoginCancellation = cancellation;
        using var profileScope = LauncherProfiles.Bind(LauncherProfiles.Selected);
        SetStatus("ProfileSigningIn");
        try
        {
            var status = await Diagnostics.CheckWithCancellationAsync(cwd, cancellation.Token);
            if (status.Code == "AuthRequired")
            {
                await RepairInteractions.LoginAsync(cwd, cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                status = await Diagnostics.CheckWithCancellationAsync(cwd, cancellation.Token);
            }
            if (_closing || _closeRequested)
                return;
            if (status.Code == "Ready")
                SetStatus("ProfileLoginDone");
            else if (status.Code == "AuthRequired")
                SetStatus("ProfileLoginCanceled");
            else
                QueueSetup(new SetupRequest(cwd, null, Examine: true));
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception error)
        {
            Debug.WriteLine(error.GetType().Name);
            SetStatus("LoginUnavailable");
        }
        finally { _profileLoginCancellation = null; }
    }
}
