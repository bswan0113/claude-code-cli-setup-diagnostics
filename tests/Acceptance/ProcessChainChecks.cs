using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

internal static partial class Program
{
    private static int RunProcessChain(string root, string release, string claude)
    {
        if (!File.Exists(release) || !File.Exists(claude) || !Environment.UserInteractive)
            throw new BlockedException("A published EXE, an installed Claude executable, and an interactive desktop are needed.");
        VerifyFixtureOnlyProfile();

        var package = MakeDirectory(root, "process-chain-package");
        var launcher = Path.Combine(package, "ClaudeLauncher.exe");
        File.Copy(release, launcher);
        using var file = File.OpenRead(launcher);
        Console.WriteLine($"SHA-256={Convert.ToHexString(SHA256.HashData(file))}");
        var passed = 0;
        var failed = 0;
        var blocked = 0;
        foreach (var (mode, option) in new[] { ("new", "--session-id"), ("resume", "--resume"), ("picker", "-r") })
        {
            try
            {
                TestGuiProcessChain(root, launcher, claude, mode, option);
                passed++;
            }
            catch (BlockedException error)
            {
                Console.WriteLine($"BLOCKED {mode} | {error.Message}");
                blocked++;
            }
            catch (Exception error)
            {
                Console.WriteLine($"FAIL {mode} | {error.GetBaseException().Message}");
                failed++;
            }
        }
        Console.WriteLine($"SUMMARY passed={passed} failed={failed} blocked={blocked}");
        return failed > 0 ? 1 : blocked > 0 ? 3 : 0;
    }

    private static void TestGuiProcessChain(string root, string launcher, string claude, string mode, string option)
    {
        var fixture = MakeDirectory(root, $"{mode}-case");
        var folder = MakeDirectory(fixture, "한글 공백 [경로] & 작업");
        var config = MakeDirectory(fixture, "claude-config");
        var project = MakeDirectory(config, Path.Combine("projects", "fixture"));
        var id = Guid.NewGuid();
        WriteSession(project, id, Older, UserLine(id, folder, "QA process chain fixture", Older));
        if (mode == "picker")
        {
            var badId = Guid.NewGuid();
            WriteSession(project, badId, Newer, JsonSerializer.Serialize(new { type = "assistant", sessionId = badId, cwd = folder }),
                "{\"bad-session\":");
        }

        var start = new ProcessStartInfo(launcher) { UseShellExecute = false, WorkingDirectory = folder };
        var credentials = start.Environment.Keys.Where(key => key.StartsWith("ANTHROPIC_", StringComparison.OrdinalIgnoreCase) ||
            key.StartsWith("CLAUDE_", StringComparison.OrdinalIgnoreCase)).ToArray();
        foreach (var key in credentials)
            start.Environment.Remove(key);
        start.Environment["CLAUDE_CONFIG_DIR"] = config;
        start.Environment["CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC"] = "1";
        start.Environment["DISABLE_AUTOUPDATER"] = "1";
        start.Environment["HTTP_PROXY"] = "http://127.0.0.1:9";
        start.Environment["HTTPS_PROXY"] = "http://127.0.0.1:9";
        start.Environment["ALL_PROXY"] = "http://127.0.0.1:9";
        start.Environment["NO_PROXY"] = "";
        start.Environment["Path"] = Path.GetDirectoryName(claude)!;
        start.Environment["DOTNET_BUNDLE_EXTRACT_BASE_DIR"] = MakeDirectory(fixture, "extract");

        using var app = Process.Start(start) ?? throw new InvalidOperationException("The GUI fixture could not be started.");
        try
        {
            var resumeButton = $"ResumeSession_{id:N}";
            WaitFor("The GUI did not load the synthetic session.", () =>
                DashboardWindow(app) is { } window && FindControl(window, resumeButton) is not null);
            if (mode == "new")
                SetInput(DashboardWindow(app)!, "FolderInput", folder);
            if (mode == "picker")
            {
                WaitFor("The GUI did not expose the damaged session's folder-specific picker.", () =>
                {
                    var window = DashboardWindow(app)!;
                    if (FindControl(window, "FolderTree") is { } tree)
                        ExpandFixtureFolders(tree);
                    return FindControl(window, "KnownFolderPicker") is not null;
                });
            }
            InvokeControl(DashboardWindow(app)!, mode switch
            {
                "new" => "OpenButton",
                "resume" => resumeButton,
                _ => "KnownFolderPicker",
            });

            ProcessEntry? shell = null;
            ProcessEntry? cli = null;
            WaitFor($"{mode}: no direct GUI → powershell.exe → claude.exe chain was observed.", () =>
            {
                var processes = ReadProcessTree();
                foreach (var candidate in processes.Where(p => p.ParentId == app.Id && p.Name.Equals("powershell.exe", StringComparison.OrdinalIgnoreCase)))
                {
                    var child = processes.FirstOrDefault(p => p.ParentId == candidate.Id && p.Name.Equals("claude.exe", StringComparison.OrdinalIgnoreCase));
                    if (child is null)
                        continue;
                    shell = candidate;
                    cli = child;
                    return true;
                }

                var explicitWrapper = processes.Where(p => p.ParentId == app.Id && p.Name.Equals("conhost.exe", StringComparison.OrdinalIgnoreCase));
                if (explicitWrapper.Any(host => processes.Any(p => p.ParentId == host.Id && p.Name.Equals("powershell.exe", StringComparison.OrdinalIgnoreCase))))
                    throw new InvalidOperationException($"{mode}: the GUI launched conhost.exe as PowerShell's parent.");
                return false;
            }, seconds: 25);

            var shellArgs = ReadOwnedArguments(shell!.Id);
            var powerShell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
            Require(shellArgs is [var shellExe, "-NoLogo", "-NoProfile", "-EncodedCommand", var bootstrap] &&
                string.Equals(Path.GetFullPath(shellExe), powerShell, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(bootstrap), $"{mode}: PowerShell did not receive the direct, no-profile bootstrap arguments.");

            var args = ReadOwnedArguments(cli!.Id);
            Require(args.Length >= 2 && string.Equals(Path.GetFullPath(args[0]), claude, StringComparison.OrdinalIgnoreCase),
                $"{mode}: the GUI did not launch the expected installed Claude executable.");
            Require(mode switch
            {
                "new" => args is [_, "--session-id", var token] && Guid.TryParseExact(token, "D", out var generated) &&
                    generated != Guid.Empty && generated != id,
                "resume" => args is [_, "--resume", var token] && Guid.TryParseExact(token, "D", out var resumed) && resumed == id,
                _ => args is [_, "-r"],
            }, $"{mode}: the Claude child did not receive the expected {option} arguments.");
            Console.WriteLine($"PASS {mode} | GUI PID {app.Id} → powershell.exe PID {shell.Id} (-NoLogo -NoProfile -EncodedCommand) → claude.exe PID {cli.Id}; {string.Join(' ', args.Skip(1))}");
        }
        finally
        {
            if (!app.HasExited)
            {
                app.Kill(entireProcessTree: true);
                app.WaitForExit(8000);
            }
        }
    }

    private static List<ProcessEntry> ReadProcessTree()
    {
        using var snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot.IsInvalid)
            throw new BlockedException($"Windows process snapshot failed with error {Marshal.GetLastWin32Error()}.");
        var entry = new NativeProcessEntry { Size = (uint)Marshal.SizeOf<NativeProcessEntry>() };
        var rows = new List<ProcessEntry>();
        for (var found = Process32First(snapshot, ref entry); found; found = Process32Next(snapshot, ref entry))
            rows.Add(new((int)entry.ProcessId, (int)entry.ParentProcessId, entry.Executable));
        return rows;
    }

    private static string[] ReadOwnedArguments(int pid)
    {
        using var process = OpenProcess(0x1000, false, pid);
        if (process.IsInvalid)
            throw new BlockedException($"Opening the fixture process failed with error {Marshal.GetLastWin32Error()}.");

        const int commandLineInformation = 60;
        _ = NtQueryInformationProcess(process, commandLineInformation, IntPtr.Zero, 0, out var size);
        if (size is < 16 or > 65536)
            throw new BlockedException("Windows did not return a usable command line buffer size for the fixture process.");
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            var status = NtQueryInformationProcess(process, commandLineInformation, buffer, size, out _);
            if (status < 0)
                throw new BlockedException($"Reading the fixture command line failed with NTSTATUS 0x{status:X8}.");
            var line = Marshal.PtrToStructure<NativeUnicodeString>(buffer);
            if (line.Length == 0 || line.Text.ToInt64() < buffer.ToInt64() ||
                line.Text.ToInt64() + line.Length > buffer.ToInt64() + size)
                throw new InvalidOperationException("The command line buffer did not contain a valid Unicode string.");
            var parsed = CommandLineToArgvW(Marshal.PtrToStringUni(line.Text, line.Length / 2)!, out var count);
            if (parsed == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                return Enumerable.Range(0, count).Select(index =>
                    Marshal.PtrToStringUni(Marshal.ReadIntPtr(parsed, index * IntPtr.Size))!).ToArray();
            }
            finally { _ = LocalFree(parsed); }
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private sealed record ProcessEntry(int Id, int ParentId, string Name);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeProcessEntry
    {
        public uint Size, Usage, ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId, Threads, ParentProcessId;
        public int BasePriority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Executable;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeUnicodeString
    {
        public ushort Length, MaximumLength;
        public IntPtr Text;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint id);

    [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32First(SafeFileHandle snapshot, ref NativeProcessEntry entry);

    [DllImport("kernel32.dll", EntryPoint = "Process32NextW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32Next(SafeFileHandle snapshot, ref NativeProcessEntry entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int id);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(SafeProcessHandle process, int informationClass, IntPtr information,
        int informationLength, out int returnLength);

    [DllImport("shell32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(string commandLine, out int count);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr address);
}
