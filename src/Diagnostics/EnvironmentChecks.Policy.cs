using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace ClaudeLauncher;

internal static partial class EnvironmentChecks
{
    private const string ExpectedPolicyVariable = "BLSLAB_CLAUDE_EXPECTED_POLICY";
    private const string SetPolicyAction = "UserRemoteSigned";
    private static readonly string ShellScript = Convert.ToBase64String(Encoding.Unicode.GetBytes("""
        $ErrorActionPreference = 'Stop'
        $ProgressPreference = 'SilentlyContinue'
        [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
        Microsoft.PowerShell.Management\Set-Location -LiteralPath ([Environment]::CurrentDirectory)
        $module = [IO.Path]::Combine($PSHOME, 'Modules', 'Microsoft.PowerShell.Security', 'Microsoft.PowerShell.Security.psd1')
        Microsoft.PowerShell.Core\Import-Module -Name $module
        $claude = @(Microsoft.PowerShell.Core\Get-Command claude -ListImported -ErrorAction SilentlyContinue)[0]
        $git = @(Microsoft.PowerShell.Core\Get-Command git.exe -CommandType Application -ListImported -ErrorAction SilentlyContinue)[0]
        function Read-Policy([string]$scope) {
            try {
                if ($scope) { return [string](Get-ExecutionPolicy -Scope $scope) }
                return [string](Get-ExecutionPolicy)
            } catch { return $null }
        }
        function Get-Sha256([IO.Stream]$stream) {
            $sha = [Security.Cryptography.SHA256]::Create()
            try { return [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '') }
            finally { $sha.Dispose() }
        }
        function Read-NpmShim($command) {
            if ($command.CommandType -ne 'ExternalScript' -or $command.Path -notmatch '^[A-Z]:\\') { return $null }
            $path = $command.Path
            if ([IO.Path]::GetFileName($path) -ine 'claude.ps1') { return $null }
            try {
                $drive = [IO.DriveInfo]::new([IO.Path]::GetPathRoot($path))
                if ($drive.DriveType -eq [IO.DriveType]::Network) { return $null }
                $directory = [IO.Path]::GetDirectoryName($path)
                $defaultPrefix = [IO.Path]::Combine([Environment]::GetFolderPath('ApplicationData'), 'npm')
                $prefix = $env:NPM_CONFIG_PREFIX
                if ($prefix) { $prefix = $prefix.TrimEnd('\', '/') }
                if ($directory -ine $defaultPrefix -and $directory -ine $prefix) { return $null }
                $package = [IO.Path]::Combine($directory, 'node_modules', '@anthropic-ai', 'claude-code', 'package.json')
                $item = [IO.FileInfo]::new($path)
                if (-not [IO.File]::Exists($package) -or -not $item.Exists -or
                    ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) { return $null }

                $file = [IO.File]::OpenRead($path)
                try {
                    if ($file.Length -gt 65536) { return $null }
                    $reader = [IO.StreamReader]::new($file, [Text.Encoding]::UTF8, $true, 1024, $true)
                    try { $text = $reader.ReadToEnd() } finally { $reader.Dispose() }
                    if ($text.Replace('\', '/') -notmatch '@anthropic-ai/claude-code/') { return $null }
                    $file.Position = 0
                    $fileHash = Get-Sha256 $file

                    $signature = $null
                    try {
                        $signature = [string](Microsoft.PowerShell.Security\Get-AuthenticodeSignature -LiteralPath $path).Status
                    } catch { }

                    $zoneId = 0
                    $zoneHash = 'Absent'
                    $streams = @(Microsoft.PowerShell.Management\Get-Item -LiteralPath $path -Stream '*')
                    if ($streams.Stream -contains 'Zone.Identifier') {
                        $zoneArgs = @{ LiteralPath = $path; Stream = 'Zone.Identifier'; Encoding = 'Byte'; TotalCount = 4097; ReadCount = 0 }
                        [byte[]]$zoneBytes = Microsoft.PowerShell.Management\Get-Content @zoneArgs
                        if (-not $zoneBytes -or $zoneBytes.Length -gt 4096) { return $null }
                        $zone = [IO.MemoryStream]::new($zoneBytes)
                        try {
                            $zoneReader = [IO.StreamReader]::new($zone, [Text.Encoding]::UTF8, $true, 1024, $true)
                            try { $zoneText = $zoneReader.ReadToEnd() } finally { $zoneReader.Dispose() }
                            if ($zoneText -notmatch '(?m)^ZoneId=(?<id>[0-4])\r?$') { return $null }
                            $zoneId = [int]$Matches.id
                            $zone.Position = 0
                            $zoneHash = Get-Sha256 $zone
                        } finally { $zone.Dispose() }
                    }
                    return @{ FileHash = $fileHash; ZoneId = $zoneId; ZoneHash = $zoneHash; SignatureStatus = $signature }
                } finally { $file.Dispose() }
            } catch { return $null }
        }
        $result = @{
            Effective = (Read-Policy)
            MachinePolicy = (Read-Policy MachinePolicy)
            UserPolicy = (Read-Policy UserPolicy)
            Process = (Read-Policy Process)
            CurrentUser = (Read-Policy CurrentUser)
            LocalMachine = (Read-Policy LocalMachine)
            ClaudeType = [string]$claude.CommandType
            ClaudePath = $claude.Path
            GitPath = $git.Path
            Shim = (Read-NpmShim $claude)
        }
        $expected = $env:BLSLAB_CLAUDE_EXPECTED_POLICY
        if ($expected) {
            $actual = @($result.Effective, $result.MachinePolicy, $result.UserPolicy, $result.Process,
                $result.CurrentUser, $result.LocalMachine, $result.ClaudeType, $result.ClaudePath,
                $result.Shim.FileHash, $result.Shim.ZoneId, $result.Shim.ZoneHash, $result.Shim.SignatureStatus) -join '|'
            if ($actual -cne $expected -or -not $result.Shim) { exit 2 }
            if ($result.MachinePolicy -ne 'Undefined' -or $result.UserPolicy -ne 'Undefined' -or
                $result.Process -ne 'Undefined' -or $result.Effective -notin 'Restricted', 'AllSigned' -or
                $result.Shim.ZoneId -ge 3 -or $result.Shim.SignatureStatus -notin 'Valid', 'NotSigned' -or
                ($result.Effective -eq 'AllSigned' -and $result.Shim.SignatureStatus -eq 'Valid')) { exit 3 }
            Set-ExecutionPolicy -Scope CurrentUser -ExecutionPolicy RemoteSigned -Force
            $result.Applied = (Get-ExecutionPolicy -Scope CurrentUser) -eq 'RemoteSigned' -and
                (Get-ExecutionPolicy) -eq 'RemoteSigned'
        }
        ConvertTo-Json -InputObject $result -Compress
        """));

    private static async Task<ShellState?> ReadShellAsync(string cwd, CancellationToken token,
        string? expected = null)
    {
        var start = new ProcessStartInfo(PowerShellProbe.Executable)
        {
            WorkingDirectory = cwd,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8
        };
        foreach (var arg in new[] { "-NoProfile", "-NoLogo", "-NonInteractive", "-EncodedCommand", ShellScript })
            start.ArgumentList.Add(arg);
        start.Environment[ExpectedPolicyVariable] = expected ?? "";
        LauncherProfiles.ApplyCliEnvironment(start.Environment);

        try
        {
            using var process = Process.Start(start);
            if (process is null)
                return null;

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(15));
            var output = process.StandardOutput.ReadToEndAsync(deadline.Token);
            var errors = process.StandardError.BaseStream.CopyToAsync(Stream.Null, deadline.Token);
            try
            {
                await Task.WhenAll(process.WaitForExitAsync(deadline.Token), output, errors).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or AggregateException) { }
                token.ThrowIfCancellationRequested();
                return null;
            }
            return process.ExitCode == 0 ? JsonSerializer.Deserialize(output.Result, DiagnosticJson.Default.ShellState) : null;
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    private static CheckItem PolicyItem(string cwd, ShellState? shell)
    {
        if (shell is null)
            return new(5, "PolicyUnknown", CheckState.Unknown);

        var evidence = $"{shell.Effective ?? "?"}\nMachinePolicy: {shell.MachinePolicy ?? "?"} · UserPolicy: {shell.UserPolicy ?? "?"}" +
            $"\nProcess: {shell.Process ?? "?"} · CurrentUser: {shell.CurrentUser ?? "?"} · LocalMachine: {shell.LocalMachine ?? "?"}";
        if (shell.ClaudePath is { } command)
            evidence += $"\n{command}";
        if (shell.ClaudeType == "Application" && Path.GetExtension(shell.ClaudePath)
            ?.Equals(".exe", StringComparison.OrdinalIgnoreCase) == true)
            return new(5, "PolicyNative", CheckState.Pass, evidence);
        if (shell.Effective is null || shell.MachinePolicy is null || shell.UserPolicy is null ||
            shell.Process is null || shell.CurrentUser is null || shell.LocalMachine is null)
            return new(5, "PolicyUnknown", CheckState.Unknown, evidence);

        if (shell.ClaudeType != "ExternalScript" || shell.Shim is not { } shim)
            return new(5, "PolicyUnconfirmed", CheckState.Info, evidence);

        evidence += $"\nAuthenticode: {shim.SignatureStatus ?? "?"} · ZoneId: {shim.ZoneId}";
        var requiresSignature = shell.Effective == "AllSigned" ||
            shell.Effective == "RemoteSigned" && shim.ZoneId >= 3;
        if (requiresSignature && shim.SignatureStatus is null or "UnknownError")
            return new(5, "PolicyUnknown", CheckState.Unknown, evidence);

        var blocked = shell.Effective == "Restricted" || requiresSignature && shim.SignatureStatus != "Valid";
        if (!blocked)
            return new(5, "PolicyNpmAllowed", CheckState.Info, evidence);

        if (shell.MachinePolicy != "Undefined" || shell.UserPolicy != "Undefined")
            return new(5, "PolicyGroup", CheckState.Failed, evidence);
        if (shell.Process != "Undefined" || shim.ZoneId >= 3 || shell.Effective == "RemoteSigned" ||
            shim.SignatureStatus is not ("Valid" or "NotSigned"))
            return new(5, "PolicyNpmManual", CheckState.Warning, evidence);

        return new(5, "PolicyNpmBlocked", CheckState.Warning, evidence, SetPolicyAction,
            "Windows PowerShell · CurrentUser → RemoteSigned")
        {
            Revision = Revision(cwd, shell.PolicyIdentity)
        };
    }

    private static async Task<bool> ApplyPolicyAsync(string cwd, CheckItem approved, CancellationToken token)
    {
        var shell = await ReadShellAsync(cwd, token).ConfigureAwait(false);
        if (shell is null || PolicyItem(cwd, shell) != approved)
            return false;
        token.ThrowIfCancellationRequested();
        RepairJournal.Prepare(new RepairJournal.UndoPlan("Policy", Previous: shell.CurrentUser));
        return await ReadShellAsync(cwd, token, shell.PolicyIdentity).ConfigureAwait(false) is { Applied: true };
    }

    private static CheckItem GitItem(ShellState? shell)
    {
        var bash = FindStandardBash();
        if (shell?.GitPath is { Length: > 0 } path)
            return new(6, "GitFound", CheckState.Pass, bash is null ? path : $"{path}\n{bash}");
        if (bash is not null)
            return new(6, "GitFound", CheckState.Pass, bash);
        return new(6, shell is null ? "GitUnknown" : "GitNotFound",
            shell is null ? CheckState.Unknown : CheckState.Info, "git.exe · bash.exe");
    }

    internal sealed record NpmShim(string FileHash, int ZoneId, string ZoneHash, string? SignatureStatus);

    internal sealed record ShellState(string? Effective, string? MachinePolicy, string? UserPolicy, string? Process,
        string? CurrentUser, string? LocalMachine, string ClaudeType, string? ClaudePath, string? GitPath,
        bool Applied = false)
    {
        public NpmShim? Shim { get; init; }

        internal string PolicyIdentity => string.Join('|', Effective, MachinePolicy, UserPolicy, Process,
            CurrentUser, LocalMachine, ClaudeType, ClaudePath, Shim?.FileHash,
            Shim?.ZoneId.ToString(System.Globalization.CultureInfo.InvariantCulture), Shim?.ZoneHash, Shim?.SignatureStatus);
    }
}
