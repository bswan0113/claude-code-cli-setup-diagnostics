using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using ClaudeLauncher;

internal static partial class Program
{
    private static string TestIndependentTerminals(string root)
    {
        var local = MakeDirectory(root, "independent-console-fixtures");
        using var first = StartSyntheticConsole(local, "A [실행] & 폴더");
        using var second = StartSyntheticConsole(local, "B [실행] & 폴더");
        WaitFor("Both synthetic PowerShell terminals did not become ready.", () => first.Ready && second.Ready);
        Require(first.ProcessId != second.ProcessId && first.ObservedDirectory == first.Folder &&
            second.ObservedDirectory == second.Folder, "Terminals did not retain distinct processes and exact working folders.");
        first.Stop();
        Require(first.Exited && !second.Exited, "Closing one terminal also closed the other terminal.");
        second.Stop();
        Require(second.Exited, "Second test terminal did not cleanly exit.");
        return "Two simultaneous PowerShell consoles retained distinct literal folders and closed independently.";
    }

    private static string TestLiveActivity(string root)
    {
        var config = MakeDirectory(root, "live-status-config");
        var state = MakeDirectory(config, "sessions");
        using var first = StartSyntheticConsole(config, "활동 [첫 번째] & 폴더");
        using var second = StartSyntheticConsole(config, "활동 [두 번째] & 폴더");
        WaitFor("The two status fixtures' console hosts did not start.", () => first.Ready && second.Ready);

        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var firstStart = first.StartFileTime;
        var secondStart = second.StartFileTime;
        var originalConfig = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        try
        {
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", config, EnvironmentVariableTarget.Process);
            var live1 = NewLiveTerminal(first, null, firstStart);
            var live2 = NewLiveTerminal(second, secondId, secondStart);
            Require(live1.Activity == "Unknown" && live2.Activity == "Unknown", "Unobserved terminal was assigned a definite status.");

            WriteActivity(state, first.ProcessId, firstId, "busy", first.Folder, firstStart, firstStart);
            WriteActivity(state, second.ProcessId, secondId, "idle", second.Folder, secondStart);
            Observe(live1);
            Observe(live2);
            Require(live1.SessionId == firstId && live1.Activity == "Working" && live1.Folder == first.Folder &&
                live2.SessionId == secondId && live2.Activity == "Waiting" && live2.Folder == second.Folder,
                "Concurrent session identity, working/waiting activity, or folder was cross-assigned.");

            WriteActivity(state, second.ProcessId, firstId, "busy", second.Folder, secondStart, secondStart - 1);
            Observe(live2);
            Require(live2.SessionId == secondId && live2.Activity == "Unknown", "An obsolete PID record was applied to the second terminal.");
            Require(live1.SessionId == firstId && live1.Activity == "Working", "Second terminal changed the first terminal's state.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", originalConfig, EnvironmentVariableTarget.Process);
        }

        return "Two live entries matched bootstrap FILETIME and the Unix-ms-only fallback; a stale FILETIME token was ignored.";
    }

    private static string TestTerminalFocus(string root)
    {
        var runtime = new DirectoryInfo(RuntimeEnvironment.GetRuntimeDirectory());
        var dotnet = Path.Combine(runtime.Parent!.Parent!.Parent!.FullName, "dotnet.exe");
        var start = new ProcessStartInfo(dotnet)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
        };
        foreach (var argument in new[] { typeof(Program).Assembly.Location, "--focus-child", root })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the no-console focus driver.");
        var output = process.StandardOutput.ReadToEndAsync();
        if (!process.WaitForExit(35000))
        {
            process.Kill(entireProcessTree: true);
            throw new InvalidOperationException("The no-console focus driver did not return.");
        }
        var message = output.GetAwaiter().GetResult().Trim();
        if (process.ExitCode == 3)
            throw new BlockedException(message);
        Require(process.ExitCode == 0, message);
        return message;
    }

    private static int RunFocusChild(string root)
    {
        try
        {
            Console.WriteLine(TestTerminalFocusCore(root));
            return 0;
        }
        catch (BlockedException error)
        {
            Console.WriteLine(error.Message);
            return 3;
        }
        catch (Exception error)
        {
            Console.WriteLine(error.GetBaseException().Message);
            return 1;
        }
    }

    private static string TestTerminalFocusCore(string root)
    {
        var local = MakeDirectory(root, "focus-console-fixtures");
        var bin = CopyUnsignedCli(local);
        using var first = StartFixtureConsole(local, bin, "Focus A");
        using var second = StartFixtureConsole(local, bin, "Focus B");
        WaitFor("The two console focus fixtures did not become ready.", () => first.Ready && second.Ready);
        if (first.Window == IntPtr.Zero || second.Window == IntPtr.Zero || !IsWindowVisible(first.Window) || !IsWindowVisible(second.Window))
            throw new BlockedException("The current desktop did not expose both plain PowerShell windows for foreground verification.");

        var originalConfig = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        try
        {
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", local, EnvironmentVariableTarget.Process);
            var live1 = NewLiveTerminal(first, Guid.NewGuid(), first.StartFileTime);
            var live2 = NewLiveTerminal(second, Guid.NewGuid(), second.StartFileTime);
            var findWindow = typeof(LiveTerminal).GetMethod("FindAttachedConsoleWindow", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new MissingMethodException(typeof(LiveTerminal).FullName, "FindAttachedConsoleWindow");
            var resolved1 = (IntPtr)findWindow.Invoke(live1, [false])!;
            var resolved2 = (IntPtr)findWindow.Invoke(live2, [false])!;
            Require((resolved1 == IntPtr.Zero || resolved1 == first.Window) &&
                (resolved2 == IntPtr.Zero || resolved2 == second.Window),
                "The product resolved a console HWND that belongs to a different window.");
            if (resolved1 == IntPtr.Zero || resolved2 == IntPtr.Zero)
            {
                var native1 = ReadFixtureConsole(first.ProcessId);
                var native2 = ReadFixtureConsole(second.ProcessId);
                if (native1.Window != first.Window || native2.Window != second.Window)
                {
                    throw new BlockedException($"The native control also could not attach to both fixture consoles. " +
                        $"Win32 errors A/B={native1.Error}/{native2.Error}.");
                }
            }
            Require(resolved1 == first.Window && resolved2 == second.Window && resolved1 != resolved2,
                "The production focus resolver did not identify each exact native test console HWND.");
            var focused1 = live1.TryFocus();
            Thread.Sleep(150);
            var matched1 = GetForegroundWindow() == first.Window;
            if (!matched1 && !CanActivateFixture(first.Window))
                throw new BlockedException("Both exact fixture HWNDs resolved. Windows also denied direct activation of the first HWND.");

            var focused2 = live2.TryFocus();
            Thread.Sleep(150);
            var matched2 = GetForegroundWindow() == second.Window;
            if (!matched2 && !CanActivateFixture(second.Window))
                throw new BlockedException("Both exact fixture HWNDs resolved. Windows also denied direct activation of the second HWND.");
            Require(focused1 && matched1 && focused2 && matched2 && first.Window != second.Window,
                $"Expected distinct fixture windows in foreground. A result/match={focused1}/{matched1}, B result/match={focused2}/{matched2}.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", originalConfig, EnvironmentVariableTarget.Process);
        }

        return "A no-console launcher process focused two distinct native console HWNDs belonging to the fixture CLI processes.";
    }

    private static bool CanActivateFixture(IntPtr window)
    {
        SetForegroundWindow(window);
        Thread.Sleep(150);
        return GetForegroundWindow() == window;
    }

    private static (IntPtr Window, int Error) ReadFixtureConsole(int process)
    {
        if (!AttachConsole(checked((uint)process)))
            return (IntPtr.Zero, Marshal.GetLastWin32Error());
        try
        {
            return (GetConsoleWindow(), 0);
        }
        finally
        {
            FreeConsole();
        }
    }

    private static LiveTerminal NewLiveTerminal(SyntheticConsole terminal, Guid? session, long fileTime) =>
        (LiveTerminal)(Activator.CreateInstance(typeof(LiveTerminal), BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null, args: [terminal.Folder, session, terminal.ProcessId, fileTime], culture: null)
            ?? throw new InvalidOperationException("Could not construct the production live terminal state."));

    private static void Observe(LiveTerminal terminal) => typeof(LiveTerminal)
        .GetMethod("Refresh", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(terminal, null);

    private static void WriteActivity(string directory, int pid, Guid sessionId, string status, string cwd,
        long fileTime, long? token = null)
    {
        var unixTime = new DateTimeOffset(DateTime.FromFileTimeUtc(fileTime)).ToUnixTimeMilliseconds();
        File.WriteAllText(Path.Combine(directory, $"{pid}.json"), JsonSerializer.Serialize(new
        {
            pid, sessionId, status, cwd, startedAt = unixTime + 1,
            kind = "interactive", entrypoint = "cli",
            procStartFt = token?.ToString(CultureInfo.InvariantCulture),
        }));
    }

    private static void WaitFor(string error, Func<bool> done, int seconds = 15)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(seconds))
        {
            if (done())
                return;
            Thread.Sleep(80);
        }
        throw new InvalidOperationException(error);
    }

    private static SyntheticConsole StartSyntheticConsole(string root, string name)
    {
        var folder = MakeDirectory(root, name);
        var shell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        const string script = """
            $ErrorActionPreference = 'Stop'
            $ProgressPreference = 'SilentlyContinue'
            Add-Type -Namespace Qa -Name ConsoleWindow -MemberDefinition '[System.Runtime.InteropServices.DllImport("kernel32.dll")] public static extern System.IntPtr GetConsoleWindow();'
            $json = ConvertTo-Json -InputObject @{ hwnd = [Qa.ConsoleWindow]::GetConsoleWindow().ToInt64(); cwd = [Environment]::CurrentDirectory } -Compress
            [IO.File]::WriteAllText($env:CLAUDE_QA_CONSOLE_READY + '.tmp', $json)
            [IO.File]::Move($env:CLAUDE_QA_CONSOLE_READY + '.tmp', $env:CLAUDE_QA_CONSOLE_READY)
            $stop = [Diagnostics.Stopwatch]::StartNew()
            while ($stop.Elapsed.TotalSeconds -lt 30 -and -not [IO.File]::Exists($env:CLAUDE_QA_CONSOLE_STOP)) {
                Start-Sleep -Milliseconds 80
            }
            """;
        var ready = Path.Combine(folder, "ready.json");
        var stop = Path.Combine(folder, "stop");
        var options = new Dictionary<string, string>
        {
            ["CLAUDE_QA_CONSOLE_READY"] = ready,
            ["CLAUDE_QA_CONSOLE_STOP"] = stop,
        };
        var command = "-NoLogo -NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        return StartTrackedConsole(shell, command, folder, ready, stop, options);
    }

    private static SyntheticConsole StartFixtureConsole(string root, string bin, string name)
    {
        var folder = MakeDirectory(root, name);
        var ready = Path.Combine(folder, "ready.json");
        var stop = Path.Combine(folder, "stop");
        var runtime = new DirectoryInfo(RuntimeEnvironment.GetRuntimeDirectory());
        var options = new Dictionary<string, string>
        {
            ["CLAUDE_QA_CONSOLE_READY"] = ready,
            [MarkerName] = "",
            ["DOTNET_ROOT"] = runtime.Parent!.Parent!.Parent!.FullName,
        };
        return StartTrackedConsole(Path.Combine(bin, "claude.exe"), $"--qa-hold \"{stop}\"", folder, ready, stop, options);
    }

    private static SyntheticConsole StartTrackedConsole(string executable, string command, string folder, string ready, string stop,
        IReadOnlyDictionary<string, string> options)
    {
        var consoleType = typeof(TerminalLauncher).Assembly.GetType("ClaudeLauncher.ConsoleProcess", throwOnError: true)!;
        var console = (WaitHandle)Call(consoleType, "Start", executable, command, folder, options)!;
        var pid = GetProcessId(console.SafeWaitHandle);
        if (pid != 0)
            return new SyntheticConsole(folder, ready, stop, console, checked((int)pid));

        File.WriteAllText(stop, "stop");
        console.WaitOne(TimeSpan.FromSeconds(5));
        console.Dispose();
        throw new InvalidOperationException("Could not obtain the synthetic console's process ID from its wait handle.");
    }

    private sealed class SyntheticConsole(string folder, string ready, string stop, WaitHandle console, int pid) : IDisposable
    {
        public string Folder => folder;
        public int ProcessId => pid;
        public long StartFileTime
        {
            get
            {
                using var process = Process.GetProcessById(pid);
                return process.StartTime.ToUniversalTime().ToFileTimeUtc();
            }
        }
        public bool Ready => File.Exists(ready);
        public bool Exited => console.WaitOne(0);
        public string ObservedDirectory => ReadReady().GetProperty("cwd").GetString()!;
        public IntPtr Window => new(ReadReady().GetProperty("hwnd").GetInt64());

        private JsonElement ReadReady()
        {
            using var json = JsonDocument.Parse(File.ReadAllText(ready));
            return json.RootElement.Clone();
        }

        public void Stop()
        {
            File.WriteAllText(stop, "stop");
            if (!console.WaitOne(TimeSpan.FromSeconds(5)))
            {
                using var process = Process.GetProcessById(pid);
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
                Require(console.WaitOne(TimeSpan.FromSeconds(5)), "A synthetic terminal did not stop.");
            }
        }

        public void Dispose()
        {
            try
            {
                if (!Exited)
                    Stop();
            }
            finally
            {
                console.Dispose();
            }
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("kernel32.dll")]
    private static extern uint GetProcessId(Microsoft.Win32.SafeHandles.SafeWaitHandle process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(uint process);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeConsole();
}
