using System.ComponentModel;
using System.Diagnostics;
using System.Security;
using System.Text;
using System.Text.Json;

namespace ClaudeLauncher;

internal static class DefenderDiagnostics
{
    private const string Inspect = """
        $ErrorActionPreference = 'Stop'
        [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
        $targets = ConvertFrom-Json -InputObject $env:BLSLAB_DEFENDER_TARGETS
        $hits = foreach ($item in @(Defender\Get-MpThreatDetection -ErrorAction Stop)) {
            if ($item.ThreatStatusID -ne 3 -or $item.ActionSuccess -ne $true) { continue }
            foreach ($resource in $item.Resources) {
                if ($resource -notmatch '^(?:file:_?)?(?<path>[A-Za-z]:\\.+)$') { continue }
                $path = $Matches['path']
                $index = -1
                for ($i = 0; $i -lt $targets.Length; $i++) {
                    if ([string]::Equals($targets[$i], $path, [StringComparison]::OrdinalIgnoreCase)) { $index = $i; break }
                }
                if ($index -ge 0) {
                    $time = $item.LastThreatStatusChangeTime
                    if ($null -eq $time) { $time = $item.InitialDetectionTime }
                    $stamp = if ($time -is [datetime]) { $time.ToUniversalTime().ToString('o') } else { '' }
                    [pscustomobject]@{ Index = $index; Stamp = $stamp }
                    break
                }
            }
        }
        $recent = $hits | Sort-Object Stamp -Descending | Select-Object -First 1
        if ($null -ne $recent) { $recent | ConvertTo-Json -Compress }
        """;

    internal static async Task<CheckItem> InspectMissingAsync(CancellationToken token)
    {
        var possible = new CheckItem(2, "DefenderPossible", CheckState.Info);
        var paths = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "claude.exe"),
            Diagnostics.UserWinGetClaude, Diagnostics.MachineWinGetClaude,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Links", "claude.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WinGet", "Links", "claude.exe")
        };
        var start = new ProcessStartInfo(PowerShellProbe.Executable)
        {
            WorkingDirectory = Environment.SystemDirectory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8
        };
        start.Environment["PSModulePath"] = string.Join(Path.PathSeparator,
            Path.Combine(Path.GetDirectoryName(PowerShellProbe.Executable)!, "Modules"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsPowerShell", "Modules"));
        start.Environment["BLSLAB_DEFENDER_TARGETS"] = JsonSerializer.Serialize(paths, DiagnosticJson.Default.StringArray);
        LauncherProfiles.ApplyCliEnvironment(start.Environment);
        foreach (var arg in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand",
            Convert.ToBase64String(Encoding.Unicode.GetBytes(Inspect)) })
            start.ArgumentList.Add(arg);
        token.ThrowIfCancellationRequested();
        try
        {
            using var process = Process.Start(start);
            if (process is null) return possible;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(6));
            try
            {
                var output = process.StandardOutput.ReadToEndAsync(deadline.Token);
                var errors = process.StandardError.BaseStream.CopyToAsync(Stream.Null, deadline.Token);
                await Task.WhenAll(process.WaitForExitAsync(deadline.Token), output, errors).ConfigureAwait(false);
                if (process.ExitCode != 0 || output.Result.Trim().Length == 0) return possible;
                using var json = JsonDocument.Parse(output.Result);
                var result = json.RootElement;
                if (!result.TryGetProperty("Index", out var index) || !index.TryGetInt32(out var i) || i < 0 || i >= paths.Length)
                    return possible;
                var time = result.TryGetProperty("Stamp", out var stamp) && stamp.TryGetDateTimeOffset(out var value)
                    ? value.ToLocalTime().ToString("yyyy-MM-dd HH:mm zzz") : "";
                return new(2, "DefenderQuarantined", CheckState.Warning, (paths[i] + Environment.NewLine + time).TrimEnd());
            }
            catch (OperationCanceledException)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { }
                token.ThrowIfCancellationRequested();
            }
        }
        catch (Exception ex) when (ex is IOException or Win32Exception or UnauthorizedAccessException or
            SecurityException or InvalidOperationException or JsonException) { }
        return possible;
    }

    internal static bool OpenProtectionHistory()
    {
        try
        {
            using var history = Process.Start(new ProcessStartInfo("windowsdefender://History") { UseShellExecute = true });
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or SecurityException)
        { return false; }
    }
}
