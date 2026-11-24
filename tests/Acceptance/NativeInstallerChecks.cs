using System.IO;
using System.Reflection;
using ClaudeLauncher;

internal static partial class Program
{
    private static string TestNativeInstallFallbackPolicy()
    {
        Require(Equals(typeof(Diagnostics).GetField("InstallerTimeout", AnyStatic)!.GetValue(null), TimeSpan.FromMinutes(5)),
            "The production inactivity window is not five minutes.");
        var resultType = typeof(Diagnostics).Assembly.GetType("ClaudeLauncher.NativeRunResult", throwOnError: true)!;
        typeof(Program).GetMethod(nameof(VerifyNativeInstallDecisions), AnyStatic)!.MakeGenericMethod(resultType)
            .Invoke(null, null);
        return "The native installer ran first. Only a stopped, silent install with a confirmed missing CLI reached WinGet. Cancellation, errors and verification failures never fell back.";
    }

    private static void VerifyNativeInstallDecisions<TRun>()
    {
        var product = typeof(Diagnostics).Assembly;
        var installer = product.GetType("ClaudeLauncher.NativeInstaller", throwOnError: true)!;
        var runState = product.GetType("ClaudeLauncher.NativeRunState", throwOnError: true)!;
        var verified = new DiagnosticResult("Ready", "fixture native executable");
        var invalidSignature = new DiagnosticResult("RepairFailed", "fixture verification failed");
        var fallbackResult = new DiagnosticResult("Ready", "fixture WinGet executable");

        (DiagnosticResult Result, List<string> Calls) Exercise(string state, DiagnosticResult? verification)
        {
            var calls = new List<string>();
            Task<TRun> Run(CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                calls.Add("native");
                var exitCode = state switch { "Completed" => (int?)0, "Failed" => 17, _ => null };
                return Task.FromResult((TRun)Activator.CreateInstance(typeof(TRun), Enum.Parse(runState, state), exitCode)!);
            }
            Task<DiagnosticResult?> Verify(bool afterStall, CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                calls.Add(afterStall ? "verify-stall" : "verify-complete");
                return Task.FromResult(verification);
            }
            Task<DiagnosticResult> Fallback(CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                calls.Add("winget");
                return Task.FromResult(fallbackResult);
            }
            var task = (Task<DiagnosticResult>)Call(installer, "InstallWithFallbackAsync",
                (Func<CancellationToken, Task<TRun>>)Run, (Func<bool, CancellationToken, Task<DiagnosticResult?>>)Verify,
                (Func<CancellationToken, Task<DiagnosticResult>>)Fallback, CancellationToken.None)!;
            return (task.GetAwaiter().GetResult(), calls);
        }

        var normal = Exercise("Completed", verified);
        Require(normal.Result == verified && normal.Calls.SequenceEqual(["native", "verify-complete"]),
            "Native success started a fallback or skipped validation.");
        var stalled = Exercise("StalledAndStopped", null);
        Require(stalled.Result == fallbackResult && stalled.Calls.SequenceEqual(["native", "verify-stall", "winget"]),
            "A stopped, silent native process with no executable did not reach exactly one WinGet fallback.");
        var silentSuccess = Exercise("StalledAndStopped", verified);
        Require(silentSuccess.Result == verified && silentSuccess.Calls.SequenceEqual(["native", "verify-stall"]),
            "WinGet ran over a verified native install that stopped reporting progress.");
        foreach (var (state, check) in new (string, DiagnosticResult?)[]
        {
            ("Completed", null), ("Completed", invalidSignature), ("StalledAndStopped", invalidSignature),
            ("Failed", null), ("StartFailed", null), ("ObservationFailed", null), ("StopUnconfirmed", null)
        })
        {
            var failure = Exercise(state, check);
            Require(failure.Result.Code != "Ready" && !failure.Calls.Contains("winget"),
                $"{state} incorrectly reported success or started WinGet after an explicit error.");
        }

        using var cancel = new CancellationTokenSource();
        var fallbackCalled = false;
        Func<CancellationToken, Task<TRun>> cancelDuringNative = token =>
        {
            cancel.Cancel();
            token.ThrowIfCancellationRequested();
            throw new InvalidOperationException("Native run did not receive the install cancellation token.");
        };
        Func<bool, CancellationToken, Task<DiagnosticResult?>> noVerification = (_, _) => Task.FromResult<DiagnosticResult?>(null);
        Func<CancellationToken, Task<DiagnosticResult>> forbiddenFallback = _ =>
        {
            fallbackCalled = true;
            return Task.FromResult(fallbackResult);
        };
        try
        {
            var task = (Task<DiagnosticResult>)Call(installer, "InstallWithFallbackAsync",
                cancelDuringNative, noVerification, forbiddenFallback, cancel.Token)!;
            _ = task.GetAwaiter().GetResult();
            throw new InvalidOperationException("Installation ignored the caller's cancellation.");
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
        Require(!fallbackCalled, "Canceled native installation unexpectedly started WinGet.");
    }

    private static string TestNativeInstallActivity(string root)
    {
        var type = typeof(Diagnostics).Assembly.GetType("ClaudeLauncher.NativeInstaller", throwOnError: true)!;
        var profile = MakeDirectory(root, "native-progress-fixture");
        var downloads = MakeDirectory(profile, Path.Combine(".claude", "downloads"));
        var file = Path.Combine(downloads, "inert-test-download.tmp");
        var quotedFile = "'" + file.Replace("'", "''", StringComparison.Ordinal) + "'";
        string Run(string script)
        {
            var task = (Task)Call(type, "RunMonitoredAsync", script, profile, TimeSpan.FromSeconds(2),
                TimeSpan.FromMilliseconds(100), CancellationToken.None)!;
            var result = CompletedResult(task);
            return result.GetType().GetProperty("State")!.GetValue(result)!.ToString()!;
        }
        Require(Run("1..9 | ForEach-Object { Write-Output 'progress'; Start-Sleep -Milliseconds 320 }; exit 0") == "Completed",
            "A responding native fixture timed out despite regular output.");
        Require(Run($"1..9 | ForEach-Object {{ [IO.File]::AppendAllText({quotedFile}, 'x'); Start-Sleep -Milliseconds 320 }}; exit 0") == "Completed",
            "A quiet native fixture timed out while its staged download kept growing.");
        Require(Run("Start-Sleep -Seconds 12; exit 0") == "StalledAndStopped",
            "A silent and inactive fixture was not stopped and verified.");
        Require(Run("exit 17") == "Failed", "An explicit nonzero exit became an inactivity timeout.");
        return "Harmless PowerShell fixtures distinguished output and download progress from inactivity, stopped the inactive job, and preserved nonzero failures. No installer or network was invoked.";
    }
}
