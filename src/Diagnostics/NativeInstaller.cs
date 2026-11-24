using System.ComponentModel;
using System.Diagnostics;
using System.Security;
using System.Text;

namespace ClaudeLauncher;

internal enum NativeRunState { Completed, Failed, StalledAndStopped, StopUnconfirmed, StartFailed, ObservationFailed }
internal sealed record NativeRunResult(NativeRunState State, int? ExitCode = null);

internal static class NativeInstaller
{
    internal static async Task<DiagnosticResult> InstallWithFallbackAsync(
        Func<CancellationToken, Task<NativeRunResult>> run,
        Func<bool, CancellationToken, Task<DiagnosticResult?>> verify,
        Func<CancellationToken, Task<DiagnosticResult>> fallback, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var native = await run(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        var stalled = native.State == NativeRunState.StalledAndStopped;
        if (stalled || native.State == NativeRunState.Completed)
        {
            var result = await verify(stalled, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (result is not null)
                return result;
            if (stalled)
                return await fallback(token).ConfigureAwait(false);
            return Failure("NativeInstallerVerificationFailed");
        }

        return Failure(native.State switch
        {
            NativeRunState.Failed => $"NativeInstallerExitFailed ({native.ExitCode})",
            NativeRunState.StartFailed => "NativeInstallerStartFailed",
            NativeRunState.StopUnconfirmed => "NativeInstallerStopUnconfirmed",
            _ => "NativeInstallerObservationFailed"
        });
    }

    private static DiagnosticResult Failure(string reason) =>
        new("RepairFailed", $"{reason}\n{Diagnostics.OfficialInstallHelp}");

    // Production always supplies Diagnostics.NativeInstallCommand; tests supply harmless local scripts.
    internal static async Task<NativeRunResult> RunMonitoredAsync(string command, string profile,
        TimeSpan inactivity, TimeSpan poll, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(PowerShellProbe.Executable)
        {
            WorkingDirectory = Environment.SystemDirectory,
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        var commandText = "if ([Console]::ReadLine() -cne 'start') { exit 1 }\n" +
            "$ErrorActionPreference = 'Stop'\n$ProgressPreference = 'SilentlyContinue'\n" +
            "try {\n" + command + "\nif (!$?) { exit 1 }\nif ($null -ne $LASTEXITCODE) { exit $LASTEXITCODE }\n" +
            "} catch { exit 1 }\nexit 0";
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(commandText));
        foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", encoded })
            start.ArgumentList.Add(argument);
        start.Environment["PSModulePath"] = Path.Combine(Path.GetDirectoryName(PowerShellProbe.Executable)!, "Modules");

        var started = false;
        try
        {
            using var job = new NativeInstallerJob();
            using var process = Process.Start(start);
            if (process is null)
                return new(NativeRunState.StartFailed);
            try
            {
                job.Attach(process);
                started = true;
            }
            finally
            {
                // Without a confirmed job never release the bootstrap or run downloaded code.
                if (!started)
                    process.StandardInput.Close();
            }

            long outputBytes = 0;
            async Task DrainAsync(Stream pipe)
            {
                var buffer = new byte[4096];
                int count;
                while ((count = await pipe.ReadAsync(buffer).ConfigureAwait(false)) != 0)
                    Interlocked.Add(ref outputBytes, count);
            }

            var output = Task.WhenAll(DrainAsync(process.StandardOutput.BaseStream),
                DrainAsync(process.StandardError.BaseStream));
            try
            {
                token.ThrowIfCancellationRequested();
                process.StandardInput.WriteLine("start");
                process.StandardInput.Close();
                var quiet = Stopwatch.StartNew();
                var previousFiles = SampleStaging(profile);
                var previous = SampleActivity(job.Sample(), Interlocked.Read(ref outputBytes));
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    var counters = job.Sample();
                    if (process.HasExited && process.ExitCode != 0)
                        return new(NativeRunState.Failed, process.ExitCode);
                    if (counters.ActiveProcesses == 0)
                    {
                        await process.WaitForExitAsync(token).ConfigureAwait(false);
                        return new(process.ExitCode == 0 ? NativeRunState.Completed : NativeRunState.Failed, process.ExitCode);
                    }
                    var activity = SampleActivity(counters, Interlocked.Read(ref outputBytes));
                    var files = SampleStaging(profile);
                    if (activity != previous || !files.SequenceEqual(previousFiles))
                        quiet.Restart();
                    previous = activity;
                    previousFiles = files;

                    if (quiet.Elapsed >= inactivity)
                    {
                        // Prefer the natural exit if it races the last sample.
                        if (job.Sample().ActiveProcesses == 0)
                            continue;
                        var stopped = await job.StopAsync().ConfigureAwait(false);
                        token.ThrowIfCancellationRequested();
                        if (!stopped)
                            return new(NativeRunState.StopUnconfirmed);
                        await process.WaitForExitAsync(token).ConfigureAwait(false);
                        if (unchecked((uint)process.ExitCode) != NativeInstallerJob.StalledExitCode)
                            return new(process.ExitCode == 0 ? NativeRunState.Completed : NativeRunState.Failed, process.ExitCode);
                        return new(NativeRunState.StalledAndStopped);
                    }
                    await Task.Delay(poll, token).ConfigureAwait(false);
                }
            }
            finally
            {
                // Closing a kill-on-close job also covers cancellation, pipe and observation failures.
                // Release the job before draining pipes that might still be held by a child.
                job.Dispose();
                await output.WaitAsync(TimeSpan.FromSeconds(8)).ConfigureAwait(false);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception or
            InvalidOperationException or SecurityException or TimeoutException)
        {
            token.ThrowIfCancellationRequested();
            return new(started ? NativeRunState.ObservationFailed : NativeRunState.StartFailed);
        }
    }

    private static (uint Processes, ulong Read, ulong Write, ulong Other, long Output) SampleActivity(
        NativeInstallerJob.Accounting info, long output) =>
        (info.TotalProcesses, info.Io.ReadBytes, info.Io.WriteBytes, info.Io.OtherBytes, output);

    private sealed record ProgressFile(string Path, long Size, long Modified);

    private static ProgressFile[] SampleStaging(string profile)
    {
        var files = new List<ProgressFile>();
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true, IgnoreInaccessible = false, AttributesToSkip = FileAttributes.ReparsePoint
        };
        void Read(string directory, string pattern)
        {
            try
            {
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                    return;
                foreach (var file in new DirectoryInfo(directory).EnumerateFiles(pattern, options))
                    files.Add(new(file.FullName, file.Length, file.LastWriteTimeUtc.Ticks));
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                // Native setup creates and renames these paths during a normal download.
            }
        }
        Read(Path.Combine(profile, ".claude", "downloads"), "*");
        Read(Path.Combine(profile, ".local", "share", "claude", "versions"), "*");
        options.RecurseSubdirectories = false;
        Read(Path.Combine(profile, ".local", "bin"), "claude*");
        return files.OrderBy(item => item.Path, StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
