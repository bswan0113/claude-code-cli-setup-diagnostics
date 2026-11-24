using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace ClaudeLauncher;

internal static class PowerShellProbe
{
    internal static string Executable { get; } = Path.Combine(
        Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

    private const string ProbePath = "CLAUDE_LAUNCHER_DIAGNOSTIC_PATH";
    private const string ProbeAuth = "CLAUDE_LAUNCHER_DIAGNOSTIC_AUTH";

    private static readonly string EncodedProbe = Convert.ToBase64String(Encoding.Unicode.GetBytes("""
        $ErrorActionPreference = 'Stop'
        $ProgressPreference = 'SilentlyContinue'
        [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
        $result = @{ Kind = 'Absent'; Path = $null; VerifiedVersion = $false; LoggedIn = $null; Issue = $null }

        try {
            $result.Issue = 'WorkingFolderUnavailable'
            Microsoft.PowerShell.Management\Set-Location -LiteralPath ([Environment]::CurrentDirectory)
            $result.Issue = 'CommandLookupFailed'
            $path = $env:CLAUDE_LAUNCHER_DIAGNOSTIC_PATH
            $checkAuth = $env:CLAUDE_LAUNCHER_DIAGNOSTIC_AUTH -eq '1'
            [Environment]::SetEnvironmentVariable('CLAUDE_LAUNCHER_DIAGNOSTIC_PATH', $null)
            [Environment]::SetEnvironmentVariable('CLAUDE_LAUNCHER_DIAGNOSTIC_AUTH', $null)

            if ($path) {
                $result.Kind = 'Application'
                $result.Path = $path
            } else {
                try {
                    $command = Microsoft.PowerShell.Core\Get-Command -Name claude -ListImported -ErrorAction Stop
                } catch [System.Management.Automation.CommandNotFoundException] {
                    $command = $null
                }
                if ($command) {
                    $result.Kind = [string]$command.CommandType
                    $result.Path = $command.Path
                }
            }

            $path = $result.Path
            $result.Issue = if ($result.Kind -eq 'Absent') { $null } else { 'UnsupportedCommandType' }
            if ($result.Kind -eq 'Application' -and $path -and
                [IO.Path]::GetExtension($path) -ieq '.exe') {
                $result.Issue = 'SignatureCheckFailed'
                # A PowerShell 7 parent can supply a module path containing incompatible assemblies.
                $securityModule = [IO.Path]::Combine($PSHOME, 'Modules', 'Microsoft.PowerShell.Security',
                    'Microsoft.PowerShell.Security.psd1')
                Microsoft.PowerShell.Core\Import-Module -Name $securityModule
                $signature = Microsoft.PowerShell.Security\Get-AuthenticodeSignature -LiteralPath $path
                $result.Issue = 'SignatureUnverified'
                if ($signature.Status -eq 'Valid' -and
                    $signature.SignerCertificate.GetNameInfo('SimpleName', $false) -ceq 'Anthropic, PBC') {
                    $result.Issue = 'VersionCheckFailed'
                    $ErrorActionPreference = 'SilentlyContinue'
                    $global:LASTEXITCODE = $null
                    $version = (& $path --version 2>$null) -join "`n"
                    $result.VerifiedVersion = $LASTEXITCODE -eq 0 -and
                        $version -match '^\s*(?<Version>\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?)\s+\(Claude Code\)\s*$'

                    if ($result.VerifiedVersion) {
                        $result.Version = $Matches['Version']
                        $result.Issue = $null
                        if ($checkAuth) {
                            $result.Issue = 'AuthStatusUnverified'
                            $global:LASTEXITCODE = $null
                            $status = (& $path auth status 2>$null) -join "`n"
                            $exitCode = $LASTEXITCODE
                            $auth = ConvertFrom-Json -InputObject $status
                            if ($auth.loggedIn -is [bool] -and $exitCode -eq [int](-not $auth.loggedIn)) {
                                $result.LoggedIn = $auth.loggedIn
                                $result.Issue = $null
                            }
                        }
                    }
                }
            }
        } catch {
            $result.Kind = 'ProbeError'
        }

        ConvertTo-Json -InputObject $result -Compress
        """));

    internal sealed record Result(string Kind, string? Path, bool VerifiedVersion, bool? LoggedIn, string? Issue)
    {
        public string? Version { get; init; }
        internal static Result Failure(string issue) => new("ProbeError", null, false, null, issue);
    }

    internal static Task<Result> RunAsync(
        string cwd, string? pathOverride = null, string? executable = null, bool checkAuth = false) =>
        RunWithCancellationAsync(cwd, CancellationToken.None, pathOverride, executable, checkAuth);

    internal static async Task<Result> RunWithCancellationAsync(string cwd, CancellationToken cancellationToken,
        string? pathOverride = null, string? executable = null, bool checkAuth = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var startInfo = new ProcessStartInfo(Executable)
        {
            WorkingDirectory = cwd,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8
        };

        foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", EncodedProbe })
            startInfo.ArgumentList.Add(argument);

        if (pathOverride is not null)
            startInfo.Environment["Path"] = pathOverride;

        startInfo.Environment[ProbePath] = executable ?? "";
        startInfo.Environment[ProbeAuth] = checkAuth ? "1" : "0";

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
                return Result.Failure("PowerShellStartFailed");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(25));
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.BaseStream.CopyToAsync(Stream.Null, timeout.Token);
            try
            {
                await Task.WhenAll(process.WaitForExitAsync(timeout.Token), stdout, stderr).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                catch (Win32Exception) { }
                cancellationToken.ThrowIfCancellationRequested();
                return Result.Failure("PowerShellTimeout");
            }

            if (process.ExitCode != 0)
                return Result.Failure($"PowerShellExitFailed (0x{unchecked((uint)process.ExitCode):X8})");

            return JsonSerializer.Deserialize<Result>(await stdout.ConfigureAwait(false))
                is { Kind: not null } result ? result : Result.Failure("PowerShellOutputInvalid");
        }
        catch (Win32Exception exception)
        {
            return Result.Failure($"PowerShellStartFailed (Win32 {exception.NativeErrorCode})");
        }
        catch (JsonException)
        {
            return Result.Failure("PowerShellOutputInvalid");
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            return Result.Failure("PowerShellProbeFailed");
        }
    }
}
