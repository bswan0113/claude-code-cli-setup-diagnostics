using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text;

namespace ClaudeLauncher;

public sealed record LaunchResult(string Status, int? ExitCode);

public static class TerminalLauncher
{
    private const int ControlCExit = unchecked((int)0xC000013A);
    private const string WorkingDirectoryVariable = "BLSLAB_CLAUDE_LAUNCHER_CWD";
    private const string ResultVariable = "BLSLAB_CLAUDE_LAUNCHER_RESULT";
    private const string ResumeVariable = "BLSLAB_CLAUDE_LAUNCHER_RESUME";

    private static readonly string PowerShell = Path.Combine(
        Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

    // The bootstrap is fixed. Per-launch values are passed in the child environment.
    private static readonly string EncodedBootstrap = Convert.ToBase64String(Encoding.Unicode.GetBytes("""
        $cwd = [Environment]::GetEnvironmentVariable('BLSLAB_CLAUDE_LAUNCHER_CWD')
        $resultPath = [Environment]::GetEnvironmentVariable('BLSLAB_CLAUDE_LAUNCHER_RESULT')
        $resume = [Environment]::GetEnvironmentVariable('BLSLAB_CLAUDE_LAUNCHER_RESUME')
        [Environment]::SetEnvironmentVariable('BLSLAB_CLAUDE_LAUNCHER_CWD', $null)
        [Environment]::SetEnvironmentVariable('BLSLAB_CLAUDE_LAUNCHER_RESULT', $null)
        [Environment]::SetEnvironmentVariable('BLSLAB_CLAUDE_LAUNCHER_RESUME', $null)
        $status = 'StartFailed'
        $exitCode = $null
        $process = $null

        try {
            Microsoft.PowerShell.Management\Set-Location -LiteralPath $cwd -ErrorAction Stop
            $command = Microsoft.PowerShell.Core\Get-Command -Name claude -ListImported -ErrorAction SilentlyContinue
            if (-not $command) {
                $status = 'CommandMissing'
            } elseif ($command.CommandType -ne 'Application' -or [IO.Path]::GetExtension($command.Path) -ine '.exe') {
                $status = 'Failed'
            } else {
                $securityModule = [IO.Path]::Combine($PSHOME, 'Modules', 'Microsoft.PowerShell.Security',
                    'Microsoft.PowerShell.Security.psd1')
                Microsoft.PowerShell.Core\Import-Module -Name $securityModule -ErrorAction Stop
                $signature = Microsoft.PowerShell.Security\Get-AuthenticodeSignature -LiteralPath $command.Path -ErrorAction Stop
                if ($signature.Status -ne 'Valid' -or
                    $signature.SignerCertificate.GetNameInfo('SimpleName', $false) -cne 'Anthropic, PBC') {
                    $status = 'Failed'
                } else {
                    $startInfo = [Diagnostics.ProcessStartInfo]::new($command.Path)
                    $startInfo.WorkingDirectory = $cwd
                    $startInfo.UseShellExecute = $false
                    if ($resume -eq '-r') {
                        $startInfo.Arguments = '-r'
                    } elseif ($resume) {
                        $id = [Guid]::ParseExact($resume, 'D')
                        $startInfo.Arguments = '--resume ' + $id.ToString('D')
                    }
                    $process = [Diagnostics.Process]::Start($startInfo)
                    $status = 'Unknown'
                    $process.WaitForExit()
                    $exitCode = $process.ExitCode
                    $status = 'CliExited'
                }
            }
        } catch {
            # Keep the stage recorded before the exception. Never send console or exception text.
        } finally {
            if ($process) { $process.Dispose() }
        }

        try {
            $result = if ($null -eq $exitCode) { $status } else {
                $status + ':' + $exitCode.ToString([Globalization.CultureInfo]::InvariantCulture)
            }
            $bytes = [Text.Encoding]::ASCII.GetBytes($result + "`n")
            $file = [IO.File]::Open($resultPath, [IO.FileMode]::Open, [IO.FileAccess]::Write,
                [IO.FileShare]'ReadWrite, Delete')
            try {
                $file.Write($bytes, 0, $bytes.Length)
            } finally {
                $file.Dispose()
            }
        } catch {
            # The GUI may have closed. No console content is sent as a fallback.
        }
        """));

    public static Task<LaunchResult> RunAsync(string cwd) => Task.Run(() => RunCoreAsync(cwd, ""));

    public static Task<LaunchResult> ResumeAsync(string cwd, Guid id) => id == Guid.Empty
        ? Task.FromResult(new LaunchResult("StartFailed", null))
        : Task.Run(() => RunCoreAsync(cwd, id.ToString("D")));

    public static Task<LaunchResult> PickSessionAsync(string cwd) => Task.Run(() => RunCoreAsync(cwd, "-r"));

    private static async Task<LaunchResult> RunCoreAsync(string cwd, string resume)
    {
        if (WorkingFolder.Normalize(cwd) is not { } folder || !Directory.Exists(folder))
        {
            return new("StartFailed", null);
        }

        try
        {
            var resultPath = Path.Combine(Path.GetTempPath(), $"blslab-claude-launch-{Guid.NewGuid():N}.result");
            using var report = new FileStream(resultPath, FileMode.CreateNew, FileAccess.ReadWrite,
                FileShare.ReadWrite | FileShare.Delete, 64, FileOptions.DeleteOnClose);
            using var shell = ConsoleProcess.Start(PowerShell, $"-NoLogo -NoProfile -EncodedCommand {EncodedBootstrap}",
                folder, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [WorkingDirectoryVariable] = folder,
                    [ResultVariable] = resultPath,
                    [ResumeVariable] = resume,
                });

            await shell.WaitForExitAsync().ConfigureAwait(false);
            return ReadResult(report);
        }
        catch (Exception error) when (error is Win32Exception or IOException or UnauthorizedAccessException
            or System.Security.SecurityException)
        {
            return new("StartFailed", null);
        }
    }

    private static LaunchResult ReadResult(FileStream report)
    {
        try
        {
            Span<byte> buffer = stackalloc byte[32];
            var length = report.ReadAtLeast(buffer, buffer.Length, false);
            if (length == 0 || length == buffer.Length || buffer[length - 1] != '\n')
            {
                return new("Unknown", null);
            }

            // The terminator distinguishes a complete result from a shell closed during its write.
            var message = Encoding.ASCII.GetString(buffer[..(length - 1)]);
            if (message.StartsWith("CliExited:", StringComparison.Ordinal) &&
                int.TryParse(message.AsSpan("CliExited:".Length), NumberStyles.AllowLeadingSign,
                    CultureInfo.InvariantCulture, out var exitCode))
            {
                return new(exitCode switch
                {
                    0 => "Exited",
                    ControlCExit => "Canceled",
                    _ => "Failed",
                }, exitCode);
            }

            if (message is "CommandMissing" or "StartFailed" or "Failed")
            {
                return new(message, null);
            }
        }
        catch (IOException)
        {
            // An absent or interrupted report does not identify the CLI outcome.
        }

        return new("Unknown", null);
    }
}
