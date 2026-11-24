using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace ClaudeLauncher;

internal static partial class InstallChecks
{
    private const string RequestVariable = "BLSLAB_CLAUDE_INSTALL_CHECK_INPUT";
    private sealed record ProcessIdentity(int Pid, int Session, long Started, string Executable, bool CanStop);
    private sealed record ProcessScan(List<ProcessIdentity> Active, bool Unverified);
    private readonly record struct BinaryState(FileStamp? File, bool Valid, string? Version, string? Issue,
        string? SignatureStatus = null);
    private sealed record ToolResult(int ExitCode, string Output);

    private static ProcessScan ReadProcesses(InstallPaths paths)
    {
        using var current = Process.GetCurrentProcess();
        using var identity = WindowsIdentity.GetCurrent();
        var active = new List<ProcessIdentity>();
        var unverified = false;
        var all = Process.GetProcesses();
        try
        {
            foreach (var process in all)
            {
                try
                {
                    var executable = process.MainModule?.FileName;
                    if (executable is null || !IsManagedExecutable(executable, paths) || process.HasExited)
                        continue;

                    var session = process.SessionId;
                    var canStop = session == current.SessionId && identity.User is { } sid && IsOwnedBy(process, sid);
                    active.Add(new(process.Id, session, process.StartTime.ToUniversalTime().Ticks, executable, canStop));
                }
                catch (Exception error) when (IsInspectionError(error))
                {
                    try
                    {
                        if (!process.HasExited && process.ProcessName.Equals("claude", StringComparison.OrdinalIgnoreCase))
                            unverified = true;
                    }
                    catch (Exception unavailable) when (IsInspectionError(unavailable)) { }
                }
            }
        }
        finally
        {
            foreach (var process in all)
                process.Dispose();
        }

        return new(active.OrderBy(item => item.Pid).ToList(), unverified);
    }

    private static bool IsManagedExecutable(string executable, InstallPaths paths)
    {
        var name = Path.GetFileName(executable);
        return (Under(executable, paths.Bin) && name.StartsWith("claude", StringComparison.OrdinalIgnoreCase)) ||
            Under(executable, paths.Versions) || Under(executable, paths.Legacy) ||
            Under(executable, paths.Downloads) ||
            ((Under(executable, paths.Profile) || Under(executable, paths.Roaming)) &&
             executable.Contains(@"\node_modules\@anthropic-ai\claude-code\", StringComparison.OrdinalIgnoreCase)) ||
            PathComparer.Equals(executable, paths.UserWinGet) || PathComparer.Equals(executable, paths.MachineWinGet);
    }

    private static bool IsOwnedBy(Process process, SecurityIdentifier sid)
    {
        // TOKEN_QUERY. The token identifies the account even when another session has the same executable.
        if (!OpenProcessToken(process.SafeHandle, 0x0008, out var token))
            return false;

        using (token)
        using (var owner = new WindowsIdentity(token.DangerousGetHandle()))
            return owner.User is { } processUser && sid.Equals(processUser);
    }

    private static bool Affects(ProcessIdentity process, IEnumerable<string> scope) =>
        scope.Any(path => PathComparer.Equals(process.Executable, path) || Under(process.Executable, path));

    private static bool FileChangeBlocked(ProcessScan processes, params string[] scope) =>
        processes.Unverified || processes.Active.Any(process => Affects(process, scope));

    private static void AddProcessChecks(List<Plan> plans, ProcessScan processes, InstallPaths paths, string[] maintenancePaths)
    {
        if (processes.Active.Count == 0 && !processes.Unverified)
            plans.Add(new(new(2, "InstallProcessesClear", CheckState.Pass)));
        if (processes.Unverified && maintenancePaths.Length != 0)
            plans.Add(new(new(2, "InstallProcessUnknown", CheckState.Unknown)));
        foreach (var process in processes.Active)
        {
            var target = $"PID {process.Pid.ToString(CultureInfo.InvariantCulture)}" + Environment.NewLine + process.Executable;
            var evidence = $"Session {process.Session.ToString(CultureInfo.InvariantCulture)}";
            if (!Affects(process, maintenancePaths))
            {
                plans.Add(new(new(2, "InstallProcessRunning", CheckState.Info, evidence, Target: target)));
            }
            else if (process.CanStop && !PathComparer.Equals(process.Executable, paths.MachineWinGet))
            {
                plans.Add(Mutation(2, "InstallProcessActive", CheckState.Warning, evidence, "StopClaudeProcess",
                    target, Fingerprint(process), token => StopAsync(process, paths, token)));
            }
            else
            {
                plans.Add(new(new(2, "InstallProcessOtherSession", CheckState.Warning, evidence, Target: target)));
            }
        }
    }

    private static async Task<bool> StopAsync(ProcessIdentity expected, InstallPaths paths, CancellationToken token)
    {
        try
        {
            using var process = Process.GetProcessById(expected.Pid);
            using var identity = WindowsIdentity.GetCurrent();
            using var current = Process.GetCurrentProcess();
            if (process.HasExited || process.SessionId != current.SessionId || process.SessionId != expected.Session ||
                process.StartTime.ToUniversalTime().Ticks != expected.Started || identity.User is not { } sid ||
                !IsOwnedBy(process, sid) || process.MainModule?.FileName is not { } executable ||
                !PathComparer.Equals(expected.Executable, executable) || !IsManagedExecutable(executable, paths) ||
                PathComparer.Equals(executable, paths.MachineWinGet))
            {
                return false;
            }

            token.ThrowIfCancellationRequested();
            process.Kill();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return false;
        }
    }

    private static async Task<BinaryState> InspectBinaryAsync(string path, CancellationToken token)
    {
        try
        {
            var attrs = File.GetAttributes(path);
            if ((attrs & FileAttributes.Directory) != 0 || Stamp(path) is not { } file)
                return new(null, false, null, "BinaryTrustUnknown");
            var probe = await PowerShellProbe.RunWithCancellationAsync(Environment.SystemDirectory, token, executable: path)
                .ConfigureAwait(false);
            return probe.VerifiedVersion && probe.Version is { } version
                ? new(file, true, version, null, probe.SignatureStatus)
                : new(file, false, null, probe.BinaryCode ?? "BinaryTrustUnknown", probe.SignatureStatus);
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return new(null, false, null, "FileMissing");
        }
        catch (Exception error) when (IsInspectionError(error))
        {
            return new(null, false, null, "BinaryTrustUnknown");
        }
    }

    // These are signature-only probes. A candidate with an updater suffix is never executed in place.
    private const string SignatureScript = """
        $ErrorActionPreference = 'Stop'
        $security = [IO.Path]::Combine($PSHOME, 'Modules', 'Microsoft.PowerShell.Security',
            'Microsoft.PowerShell.Security.psd1')
        Microsoft.PowerShell.Core\Import-Module -Name $security
        $request = Microsoft.PowerShell.Utility\ConvertFrom-Json $env:BLSLAB_CLAUDE_INSTALL_CHECK_INPUT
        $signed = @(foreach ($path in $request.Paths) {
            $signature = Microsoft.PowerShell.Security\Get-AuthenticodeSignature -LiteralPath $path -ErrorAction SilentlyContinue
            if ($signature.Status -eq 'Valid' -and
                $signature.SignerCertificate.GetNameInfo('SimpleName', $false) -cin $request.Publishers) {
                [string]$path
            }
        })
        Microsoft.PowerShell.Utility\ConvertTo-Json -InputObject $signed -Compress
        """;

    private static async Task<IReadOnlyList<string>> FindSignedFilesAsync(IEnumerable<string> candidates, string[] publishers,
        CancellationToken token)
    {
        var signed = new List<string>();
        foreach (var batch in candidates.Chunk(12))
        {
            var result = await RunScriptAsync(SignatureScript, new { Paths = batch, Publishers = publishers }, token)
                .ConfigureAwait(false);
            if (result is { ExitCode: 0, Output.Length: > 0 } &&
                JsonSerializer.Deserialize<string[]>(result.Output) is { } matches)
            {
                signed.AddRange(matches.Where(path => batch.Contains(path, PathComparer)));
            }
        }

        return signed;
    }

    private static Task<ToolResult?> RunScriptAsync(string script, object input, CancellationToken token) =>
        RunPowerShellAsync(script, token, env => env[RequestVariable] = JsonSerializer.Serialize(input));

    private static Task<ToolResult?> RunPowerShellAsync(string script, CancellationToken token,
        Action<IDictionary<string, string?>> environment)
    {
        var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(
            "[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)\n" + script));
        return RunToolAsync(PowerShellProbe.Executable,
            ["-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", encoded], token,
            TimeSpan.FromSeconds(35), environment);
    }

    private static async Task<ToolResult?> RunToolAsync(string executable, IEnumerable<string> arguments,
        CancellationToken token, TimeSpan limit, Action<IDictionary<string, string?>>? environment = null)
    {
        token.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, WorkingDirectory = Environment.SystemDirectory, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        environment?.Invoke(start.Environment);

        using var process = Process.Start(start);
        if (process is null)
            return null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(limit);
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var error = process.StandardError.BaseStream.CopyToAsync(Stream.Null, timeout.Token);
        try
        {
            await Task.WhenAll(process.WaitForExitAsync(timeout.Token), output, error).ConfigureAwait(false);
            return new(process.ExitCode, await output.ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch (Exception failure) when (IsInspectionError(failure)) { }
            using var drain = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try { await process.WaitForExitAsync(drain.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            token.ThrowIfCancellationRequested();
            return null;
        }
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
}
