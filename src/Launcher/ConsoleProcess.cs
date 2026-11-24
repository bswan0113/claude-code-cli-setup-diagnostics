using System.Collections;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ClaudeLauncher;

internal sealed class ConsoleProcess : WaitHandle
{
    private const uint CreateNewConsole = 0x00000010;
    private const uint CreateUnicodeEnvironment = 0x00000400;

    private ConsoleProcess(IntPtr handle, uint id)
    {
        SafeWaitHandle = new SafeWaitHandle(handle, ownsHandle: true);
        Id = id;
    }

    internal uint Id { get; }

    internal static ConsoleProcess Start(string executable, string arguments, string cwd,
        IReadOnlyDictionary<string, string> overrides)
    {
        var variables = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            variables[(string)entry.Key] = (string)entry.Value!;
        }

        foreach (var (name, value) in overrides)
        {
            variables[name] = value;
        }

        var environmentBlock = new StringBuilder();
        foreach (var (name, value) in variables)
        {
            environmentBlock.Append(name).Append('=').Append(value).Append('\0');
        }

        environmentBlock.Append('\0');
        var environment = Marshal.StringToHGlobalUni(environmentBlock.ToString());
        try
        {
            var commandLine = new StringBuilder($"\"{executable}\" {arguments}");
            var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>() };
            if (!CreateProcess(executable, commandLine, IntPtr.Zero, IntPtr.Zero, false,
                CreateNewConsole | CreateUnicodeEnvironment, environment, cwd, ref startup, out var process))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            using var primaryThread = new SafeWaitHandle(process.Thread, ownsHandle: true);
            return new ConsoleProcess(process.Process, process.ProcessId);
        }
        finally
        {
            Marshal.FreeHGlobal(environment);
        }
    }

    internal async Task WaitForExitAsync()
    {
        var exit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = ThreadPool.RegisterWaitForSingleObject(this, (_, _) => exit.TrySetResult(),
            state: null, Timeout.Infinite, executeOnlyOnce: true);
        try
        {
            await exit.Task.ConfigureAwait(false);
        }
        finally
        {
            registration.Unregister(null);
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", ExactSpelling = true,
        CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcess(string application, StringBuilder commandLine,
        IntPtr processAttributes, IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags, IntPtr environment, string currentDirectory, ref StartupInfo startup,
        out ProcessInformation process);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public IntPtr Reserved;
        public IntPtr Desktop;
        public IntPtr Title;
        public int X;
        public int Y;
        public int XSize;
        public int YSize;
        public int XCountChars;
        public int YCountChars;
        public int FillAttribute;
        public int Flags;
        public short ShowWindow;
        public short ReservedSize;
        public IntPtr ReservedData;
        public IntPtr StandardInput;
        public IntPtr StandardOutput;
        public IntPtr StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process;
        public IntPtr Thread;
        public uint ProcessId;
        public uint ThreadId;
    }
}
