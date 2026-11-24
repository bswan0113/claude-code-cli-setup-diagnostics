using System.ComponentModel;
using System.Security;
using System.Text;

namespace ClaudeLauncher;

internal static class RepairInteractions
{
    private static readonly string LoginScript = Convert.ToBase64String(Encoding.Unicode.GetBytes("""
        $ErrorActionPreference = 'Stop'
        $status = 1
        $cli = $null
        $lock = $null
        $cancel = $null
        try {
            $exe = $env:BLSLAB_CLAUDE_LOGIN_EXE
            $cancel = [Threading.EventWaitHandle]::OpenExisting($env:BLSLAB_CLAUDE_LOGIN_CANCEL)
            [Environment]::SetEnvironmentVariable('BLSLAB_CLAUDE_LOGIN_EXE', $null)
            [Environment]::SetEnvironmentVariable('BLSLAB_CLAUDE_LOGIN_CANCEL', $null)
            $module = [IO.Path]::Combine($PSHOME, 'Modules', 'Microsoft.PowerShell.Security', 'Microsoft.PowerShell.Security.psd1')
            Microsoft.PowerShell.Core\Import-Module -Name $module
            $lock = [IO.File]::Open($exe, 'Open', 'Read', 'Read')
            $sig = Microsoft.PowerShell.Security\Get-AuthenticodeSignature -LiteralPath $exe
            if ($cancel.WaitOne(0) -or $sig.Status -ne 'Valid' -or
                $sig.SignerCertificate.GetNameInfo('SimpleName', $false) -cne 'Anthropic, PBC') { exit 1 }
            [Console]::Title = 'Claude Code · PowerShell'
            $start = [Diagnostics.ProcessStartInfo]::new($exe, 'auth login')
            $start.WorkingDirectory = [Environment]::CurrentDirectory
            $start.UseShellExecute = $false
            $cli = [Diagnostics.Process]::Start($start)
            $lock.Dispose()
            while (-not $cli.WaitForExit(200)) {
                if ($cancel.WaitOne(0)) {
                    # Stop this sign-in command only. The browser may belong to the user.
                    $cli.Kill()
                    [void]$cli.WaitForExit(3000)
                    exit 2
                }
            }
            $status = 0
        } catch { } finally {
            if ($cli) { $cli.Dispose() }
            if ($lock) { $lock.Dispose() }
            if ($cancel) { $cancel.Dispose() }
        }
        exit $status
        """));

    // The caller checks auth status again even if the user closed the terminal or canceled sign-in.
    internal static async Task<bool> LoginAsync(string cwd, CancellationToken token)
    {
        try
        {
            if (WorkingFolder.Normalize(cwd) is not { } folder || !Directory.Exists(folder))
                return false;
            var probe = await PowerShellProbe.RunWithCancellationAsync(folder, token, checkAuth: true).ConfigureAwait(false);
            if (probe is not { Kind: "Application", VerifiedVersion: true, LoggedIn: false, Path: { } executable })
                return false;

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromMinutes(15));
            var eventName = @"Local\BlsLab.ClaudeLauncher.Login." + Guid.NewGuid().ToString("N");
            using var stop = new EventWaitHandle(false, EventResetMode.ManualReset, eventName);
            token.ThrowIfCancellationRequested();
            using var shell = ConsoleProcess.Start(PowerShellProbe.Executable,
                "-NoLogo -NoProfile -EncodedCommand " + LoginScript, folder,
                new Dictionary<string, string>
                {
                    ["BLSLAB_CLAUDE_LOGIN_EXE"] = executable,
                    ["BLSLAB_CLAUDE_LOGIN_CANCEL"] = eventName
                });
            var exited = shell.WaitForExitAsync();
            try
            {
                await exited.WaitAsync(deadline.Token).ConfigureAwait(false);
                return shell.ExitedWith(0);
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                stop.Set();
                try { await exited.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
                catch (TimeoutException) { shell.StopShell(); }
                return false;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return false; }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException or SecurityException)
        {
            return false;
        }
    }
}
