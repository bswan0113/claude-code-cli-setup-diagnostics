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
    private const string LiveVariable = "BLSLAB_CLAUDE_LAUNCHER_LIVE";
    private const string NewSessionVariable = "BLSLAB_CLAUDE_LAUNCHER_NEW_SESSION";

    private static readonly string PowerShell = Path.Combine(
        Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

    // The bootstrap is fixed. Per-launch values are passed in the child environment.
    private static readonly string EncodedBootstrap = Convert.ToBase64String(Encoding.Unicode.GetBytes(SignedExecutable.ResolvePath + "\n" + """
        function Write-LauncherReport([string]$path, [string]$value) {
            $bytes = [Text.Encoding]::ASCII.GetBytes($value + "`n")
            $file = [IO.File]::Open($path, [IO.FileMode]::Open, [IO.FileAccess]::Write,
                [IO.FileShare]'ReadWrite, Delete')
            try { $file.Write($bytes, 0, $bytes.Length) } finally { $file.Dispose() }
        }

        $cwd = [Environment]::GetEnvironmentVariable('BLSLAB_CLAUDE_LAUNCHER_CWD')
        $resultPath = [Environment]::GetEnvironmentVariable('BLSLAB_CLAUDE_LAUNCHER_RESULT')
        $resume = [Environment]::GetEnvironmentVariable('BLSLAB_CLAUDE_LAUNCHER_RESUME')
        $livePath = [Environment]::GetEnvironmentVariable('BLSLAB_CLAUDE_LAUNCHER_LIVE')
        $newSession = [Environment]::GetEnvironmentVariable('BLSLAB_CLAUDE_LAUNCHER_NEW_SESSION')
        [Environment]::SetEnvironmentVariable('BLSLAB_CLAUDE_LAUNCHER_CWD', $null)
        [Environment]::SetEnvironmentVariable('BLSLAB_CLAUDE_LAUNCHER_RESULT', $null)
        [Environment]::SetEnvironmentVariable('BLSLAB_CLAUDE_LAUNCHER_RESUME', $null)
        [Environment]::SetEnvironmentVariable('BLSLAB_CLAUDE_LAUNCHER_LIVE', $null)
        [Environment]::SetEnvironmentVariable('BLSLAB_CLAUDE_LAUNCHER_NEW_SESSION', $null)
        $status = 'StartFailed'
        $exitCode = $null
        $process = $null
        $exeLock = $null

        try { [Console]::Title = 'Windows PowerShell' } catch { }

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
                $exePath = Resolve-LauncherExecutable $command.Path
                $exeLock = [IO.File]::Open($exePath, 'Open', 'Read', 'Read')
                $signature = Microsoft.PowerShell.Security\Get-AuthenticodeSignature -LiteralPath $exePath -ErrorAction Stop
                if ($signature.Status -ne 'Valid' -or
                    $signature.SignerCertificate.GetNameInfo('SimpleName', $false) -cne 'Anthropic, PBC') {
                    $status = 'Failed'
                } else {
                    $startInfo = [Diagnostics.ProcessStartInfo]::new($exePath)
                    $startInfo.WorkingDirectory = $cwd
                    $startInfo.UseShellExecute = $false
                    if ($resume -eq '-r') {
                        $startInfo.Arguments = '-r'
                    } elseif ($resume) {
                        $id = [Guid]::ParseExact($resume, 'D')
                        $startInfo.Arguments = '--resume ' + $id.ToString('D')
                    } elseif ($newSession) {
                        $id = [Guid]::ParseExact($newSession, 'D')
                        $startInfo.Arguments = '--session-id ' + $id.ToString('D')
                    }
                    $process = [Diagnostics.Process]::Start($startInfo)
                    $exeLock.Dispose()
                    $exeLock = $null
                    $status = 'Unknown'
                    if ($livePath) {
                        try {
                            $culture = [Globalization.CultureInfo]::InvariantCulture
                            $live = $process.Id.ToString($culture) + ':' + $process.StartTime.ToUniversalTime().ToFileTimeUtc().ToString($culture)
                            Write-LauncherReport $livePath $live
                        } catch {
                            # A tracking failure must not interrupt the interactive CLI.
                        }
                    }
                    $process.WaitForExit()
                    $exitCode = $process.ExitCode
                    $status = 'CliExited'
                }
            }
        } catch {
            # Keep the stage recorded before the exception. Never send console or exception text.
        } finally {
            if ($exeLock) { $exeLock.Dispose() }
            if ($process) { $process.Dispose() }
        }

        try {
            $result = if ($null -eq $exitCode) { $status } else {
                $status + ':' + $exitCode.ToString([Globalization.CultureInfo]::InvariantCulture)
            }
            Write-LauncherReport $resultPath $result
        } catch {
            # The GUI may have closed. No console content is sent as a fallback.
        }
        """));

    public static Task<LaunchResult> RunAsync(string cwd) => Task.Run(() => RunCoreAsync(cwd, ""));

    public static Task<LaunchResult> ResumeAsync(string cwd, Guid id) => id == Guid.Empty
        ? Task.FromResult(new LaunchResult("StartFailed", null))
        : Task.Run(() => RunCoreAsync(cwd, id.ToString("D")));

    public static Task<LaunchResult> PickSessionAsync(string cwd) => Task.Run(() => RunCoreAsync(cwd, "-r"));

    public static Task<LaunchResult> LaunchAsync(string cwd, Guid? sessionId, bool picker, Action<LiveTerminal> onStarted)
    {
        ArgumentNullException.ThrowIfNull(onStarted);
        if (sessionId == Guid.Empty || (sessionId.HasValue && picker))
        {
            return Task.FromResult(new LaunchResult("StartFailed", null));
        }

        var newSession = !picker && sessionId is null ? Guid.NewGuid() : (Guid?)null;
        var resume = picker ? "-r" : sessionId?.ToString("D") ?? "";
        var progress = new Progress<LiveTerminal>(onStarted);
        return Task.Run(() => RunCoreAsync(cwd, resume, sessionId ?? newSession, newSession, progress));
    }

    private static async Task<LaunchResult> RunCoreAsync(string cwd, string resume, Guid? sessionId = null,
        Guid? newSession = null, IProgress<LiveTerminal>? progress = null)
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
            var livePath = resultPath + ".live";
            using var liveReport = progress is null ? null : new FileStream(livePath, FileMode.CreateNew,
                FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete, 64, FileOptions.DeleteOnClose);
            var arguments = $"-NoLogo -NoProfile -EncodedCommand {EncodedBootstrap}";
            using var shell = ConsoleProcess.Start(PowerShell, arguments,
                folder, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [WorkingDirectoryVariable] = folder,
                    [ResultVariable] = resultPath,
                    [ResumeVariable] = resume,
                    [LiveVariable] = liveReport is null ? "" : livePath,
                    [NewSessionVariable] = newSession?.ToString("D") ?? "",
                });

            var exited = shell.WaitForExitAsync();
            var tracking = liveReport is not null && progress is not null
                ? TrackAsync(folder, sessionId, liveReport, progress, exited)
                : Task.CompletedTask;
            await exited.ConfigureAwait(false);
            try
            {
                await tracking.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The tracker cannot change the CLI's lifetime or turn an observation error into a start failure.
            }

            return ReadResult(report);
        }
        catch (Exception error) when (error is Win32Exception or IOException or UnauthorizedAccessException
            or System.Security.SecurityException)
        {
            return new("StartFailed", null);
        }
    }

    private static async Task TrackAsync(string folder, Guid? sessionId, FileStream report,
        IProgress<LiveTerminal> progress, Task exited)
    {
        LiveTerminal? live = null;
        try
        {
            while (!exited.IsCompleted)
            {
                if (live is null && ReadLiveIdentity(report) is { } identity)
                {
                    live = new LiveTerminal(folder, sessionId, identity.Pid, identity.StartTime);
                    live.Refresh();
                    progress.Report(live);
                }
                else
                {
                    live?.Refresh();
                }

                await Task.WhenAny(exited, Task.Delay(live is null ? 100 : 500)).ConfigureAwait(false);
            }
        }
        finally
        {
            live?.MarkClosed();
        }
    }

    private static (int Pid, long StartTime)? ReadLiveIdentity(FileStream report)
    {
        report.Position = 0;
        Span<byte> buffer = stackalloc byte[48];
        var length = report.ReadAtLeast(buffer, buffer.Length, false);
        if (length == 0 || length == buffer.Length || buffer[length - 1] != '\n')
        {
            return null;
        }

        var value = Encoding.ASCII.GetString(buffer[..(length - 1)]).AsSpan();
        var separator = value.IndexOf(':');
        if (separator > 0 && int.TryParse(value[..separator], NumberStyles.None, CultureInfo.InvariantCulture, out var pid) &&
            long.TryParse(value[(separator + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var start) &&
            pid > 0 && start > 0)
        {
            return (pid, start);
        }

        return null;
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
