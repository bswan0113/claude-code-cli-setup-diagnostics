using System.Diagnostics;

namespace ClaudeLauncher;

public partial class MainWindow
{
    private async Task SignInToCurrentProfileAsync()
    {
        // Auth status and interactive sign-in use the same profile-bound child environment as terminals.
        var cwd = Environment.SystemDirectory;
        SetStatus("ProfileSigningIn");
        try
        {
            var status = await Diagnostics.CheckAsync(cwd);
            if (status.Code == "AuthRequired")
            {
                await RepairInteractions.LoginAsync(cwd, CancellationToken.None);
                status = await Diagnostics.CheckAsync(cwd);
            }
            if (_closing)
                return;
            if (status.Code == "Ready")
                SetStatus("ProfileLoginDone");
            else if (status.Code == "AuthRequired")
                SetStatus("ProfileLoginCanceled");
            else
                QueueSetup(new SetupRequest(cwd, null, Examine: true));
        }
        catch (Exception error)
        {
            Debug.WriteLine(error.GetType().Name);
            SetStatus("LoginUnavailable");
        }
    }
}
