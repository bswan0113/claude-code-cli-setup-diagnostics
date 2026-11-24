using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using ClaudeLauncher;

internal static partial class Program
{
    private static string TestResumeArguments(string root)
    {
        var folder = MakeDirectory(root, "재개 검증 [작업 폴더] & 테스트");
        var field = typeof(TerminalLauncher).GetField("EncodedBootstrap", AnyStatic)
            ?? throw new MissingFieldException(typeof(TerminalLauncher).FullName, "EncodedBootstrap");
        var bootstrap = Encoding.Unicode.GetString(Convert.FromBase64String((string)field.GetValue(null)!));
        const string first = "$startInfo = [Diagnostics.ProcessStartInfo]::new($command.Path)";
        const string last = "$process = [Diagnostics.Process]::Start($startInfo)";
        var begin = bootstrap.IndexOf(first, StringComparison.Ordinal);
        var end = bootstrap.IndexOf(last, StringComparison.Ordinal);
        Require(begin >= 0 && end > begin, "The production command data builder could not be located.");

        // Evaluate only the data-building statements. This test never starts the target CLI.
        var script = $$"""
            $ErrorActionPreference = 'Stop'
            $ProgressPreference = 'SilentlyContinue'
            [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
            $command = @{ Path = $env:CLAUDE_QA_EXECUTABLE }
            $cwd = $env:CLAUDE_QA_CWD
            foreach ($resume in @($env:CLAUDE_QA_SESSION_ID, '-r', '')) {
                {{bootstrap[begin..end]}}
                [pscustomobject]@{ cwd = $startInfo.WorkingDirectory; args = $startInfo.Arguments } |
                    ConvertTo-Json -Compress
            }
            """;
        var powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        var start = new ProcessStartInfo(powershell)
        {
            WorkingDirectory = folder, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, StandardOutputEncoding = Encoding.UTF8,
        };
        var id = Guid.NewGuid().ToString("D");
        start.Environment["CLAUDE_QA_EXECUTABLE"] = Path.Combine(folder, "never-started.exe");
        start.Environment["CLAUDE_QA_CWD"] = folder;
        start.Environment["CLAUDE_QA_SESSION_ID"] = id;
        foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand",
            Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) })
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("PowerShell argument check did not start.");
        var output = process.StandardOutput.ReadToEndAsync();
        if (!process.WaitForExit(10000))
        {
            process.Kill(entireProcessTree: true);
            throw new InvalidOperationException("PowerShell argument check timed out.");
        }
        var rows = output.GetAwaiter().GetResult().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Require(process.ExitCode == 0 && rows.Length == 3, "Expected resume, picker and new-session command data.");
        CheckCommandData(rows[0], folder, $"--resume {id}");
        CheckCommandData(rows[1], folder, "-r");
        CheckCommandData(rows[2], folder, "");
        return "Production PowerShell assigned --resume <GUID>, -r, and empty arguments with the exact literal working directory.";
    }

    private static void CheckCommandData(string json, string folder, string expectedArguments)
    {
        using var document = JsonDocument.Parse(json);
        Require(document.RootElement.GetProperty("cwd").GetString() == folder &&
            document.RootElement.GetProperty("args").GetString() == expectedArguments,
            $"The production command builder changed the working directory or arguments for '{expectedArguments}'.");
    }

    private static string TestResumeWithUnsignedCli(string root)
    {
        var folder = MakeDirectory(root, "재개 작업 [검증] & 폴더");
        var bin = CopyUnsignedCli(MakeDirectory(root, "resume-fixture"));
        var marker = Path.Combine(root, "unsigned-resume-called.txt");
        var originalPath = Environment.GetEnvironmentVariable("Path");
        var originalMarker = Environment.GetEnvironmentVariable(MarkerName);
        try
        {
            Environment.SetEnvironmentVariable("Path", bin, EnvironmentVariableTarget.Process);
            Environment.SetEnvironmentVariable(MarkerName, marker, EnvironmentVariableTarget.Process);
            CheckUnsignedCliMarker(bin, folder, marker);

            var id = Guid.NewGuid();
            var resume = TerminalLauncher.ResumeAsync(folder, id).WaitAsync(TimeSpan.FromSeconds(12)).GetAwaiter().GetResult();
            var picker = TerminalLauncher.PickSessionAsync(folder).WaitAsync(TimeSpan.FromSeconds(12)).GetAwaiter().GetResult();
            Require(resume is { Status: "Failed", ExitCode: null } && picker is { Status: "Failed", ExitCode: null },
                $"Expected an unverified candidate without a CLI exit code. Resume={resume.Status}, picker={picker.Status}.");
            Require(!File.Exists(marker), "The launcher executed the unsigned candidate.");

            var emptyId = TerminalLauncher.ResumeAsync(folder, Guid.Empty).GetAwaiter().GetResult();
            var missingFolder = TerminalLauncher.PickSessionAsync(Path.Combine(folder, "missing")).GetAwaiter().GetResult();
            Require(emptyId.Status == "StartFailed" && missingFolder.Status == "StartFailed",
                "Empty session GUID or missing picker folder was accepted.");
            return "Real resume and picker entry points rejected the unsigned CLI, empty GUID, and missing picker folder.";
        }
        finally
        {
            Environment.SetEnvironmentVariable("Path", originalPath, EnvironmentVariableTarget.Process);
            Environment.SetEnvironmentVariable(MarkerName, originalMarker, EnvironmentVariableTarget.Process);
        }
    }
}
