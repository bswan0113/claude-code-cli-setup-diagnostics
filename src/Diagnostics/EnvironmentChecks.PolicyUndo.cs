using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace ClaudeLauncher;

internal static partial class EnvironmentChecks
{
    internal static async Task<bool> RestoreUserPolicyAsync(string? previous, CancellationToken token)
    {
        if (previous is not ("Restricted" or "AllSigned" or "Default" or "Undefined"))
            return false;
        const string command = """
            $ErrorActionPreference = 'Stop'
            $policy = $env:BLSLAB_RESTORE_USER_POLICY
            if ($policy -notin 'Restricted', 'AllSigned', 'Default', 'Undefined') { exit 2 }
            if ((Get-ExecutionPolicy -Scope MachinePolicy) -ne 'Undefined' -or
                (Get-ExecutionPolicy -Scope UserPolicy) -ne 'Undefined' -or
                (Get-ExecutionPolicy -Scope Process) -ne 'Undefined') { exit 3 }
            $current = Get-ExecutionPolicy -Scope CurrentUser
            if ($current -eq $policy) { exit 0 }
            if ($current -ne 'RemoteSigned') { exit 4 }
            Set-ExecutionPolicy -Scope CurrentUser -ExecutionPolicy $policy -Force
            if ((Get-ExecutionPolicy -Scope CurrentUser) -ne $policy) { exit 5 }
            """;
        var start = new ProcessStartInfo(PowerShellProbe.Executable)
        {
            WorkingDirectory = Environment.SystemDirectory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardError = true, RedirectStandardOutput = true
        };
        foreach (var arg in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand",
            Convert.ToBase64String(Encoding.Unicode.GetBytes(command)) })
            start.ArgumentList.Add(arg);
        start.Environment["PSModulePath"] = Path.Combine(Path.GetDirectoryName(PowerShellProbe.Executable)!, "Modules");
        start.Environment["BLSLAB_RESTORE_USER_POLICY"] = previous;
        token.ThrowIfCancellationRequested();
        using var process = Process.Start(start);
        if (process is null)
            return false;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            await Task.WhenAll(process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, timeout.Token),
                process.StandardError.BaseStream.CopyToAsync(Stream.Null, timeout.Token),
                process.WaitForExitAsync(timeout.Token)).ConfigureAwait(false);
            return process.ExitCode == 0;
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception error) when (error is Win32Exception or InvalidOperationException) { }
            token.ThrowIfCancellationRequested();
            return false;
        }
    }
}
