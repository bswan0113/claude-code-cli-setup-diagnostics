using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Automation;

namespace ClaudeLauncher;

public sealed class LiveTerminal
{
    private const uint RootOwner = 3;
    private static readonly object ConsoleAttachment = new();

    private readonly int _claudePid;
    private readonly long _processStart;
    private readonly string _statePath;
    private Snapshot _snapshot;
    private Guid? _pendingSessionId;
    private int _closed;

    internal LiveTerminal(string folder, Guid? sessionId, int claudePid, long processStart)
        : this(folder, claudePid, processStart,
            Path.Combine(SessionDirectory(), claudePid.ToString(CultureInfo.InvariantCulture) + ".json"),
            new(sessionId, "Unknown"))
    {
        _pendingSessionId = sessionId;
    }

    private LiveTerminal(string folder, int claudePid, long processStart,
        string statePath, Snapshot snapshot)
    {
        Folder = folder;
        _claudePid = claudePid;
        _processStart = processStart;
        _statePath = statePath;
        _snapshot = snapshot;
    }

    public Guid? SessionId => Volatile.Read(ref _snapshot).Id;
    public string Activity => Volatile.Read(ref _snapshot).Activity;
    internal bool IsSubagent => Volatile.Read(ref _snapshot).IsSubagent;
    public string Folder { get; }
    internal int ProcessId => _claudePid;
    internal DateTime StartedUtc => DateTime.FromFileTimeUtc(_processStart);

    public static IReadOnlyList<LiveTerminal> Discover()
    {
        var live = new List<LiveTerminal>();
        try
        {
            foreach (var path in Directory.EnumerateFiles(SessionDirectory(), "*.json"))
            {
                if (ReadState(path) is not { } state || state.SessionId is null || state.IsSubagent ||
                    state.EntryPoint is "claude-desktop" or "claude-desktop-3p" or "local-agent" or "claude-vscode" ||
                    (state.Kind is not null && state.Kind != "interactive") ||
                    Path.GetFileNameWithoutExtension(path) != state.Pid.ToString(CultureInfo.InvariantCulture) ||
                    WorkingFolder.Normalize(state.Cwd) is not { } cwd || WorkingFolder.IsClaudeScratchpad(cwd) ||
                    FindProcessStart(state.Pid) is not { } start || !state.MatchesProcess(state.Pid, start))
                {
                    continue;
                }

                live.Add(new(cwd, state.Pid, start, path, new(state.SessionId, state.Activity)));
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException
            or NotSupportedException)
        {
            // Discovery is optional when the CLI hasn't created its session registry yet.
        }

        return live;
    }

    // Deletion also needs to protect sessions that the dashboard intentionally does not display.
    internal static bool HasActiveProject(string cwd)
    {
        try
        {
            foreach (var path in Directory.EnumerateFiles(SessionDirectory(), "*.json"))
            {
                if (!int.TryParse(Path.GetFileNameWithoutExtension(path), out var pid) || pid <= 0)
                    continue;
                var state = ReadState(path);
                if (WorkingFolder.Normalize(state?.Cwd) is { } registered &&
                    !registered.Equals(cwd, StringComparison.OrdinalIgnoreCase))
                    continue;
                try
                {
                    using var process = Process.GetProcessById(pid);
                    if (!process.HasExited && (state is null || state.MatchesProcess(pid, process.StartTime.ToUniversalTime().ToFileTimeUtc())))
                        return true;
                }
                catch (ArgumentException) { } // The registry can outlive its process.
                catch (InvalidOperationException) { } // Process exited during inspection.
            }
        }
        catch (DirectoryNotFoundException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception
            or System.Security.SecurityException or NotSupportedException) { return true; }
        return false;
    }

    public bool TryFocus()
    {
        if (Volatile.Read(ref _closed) != 0)
        {
            return false;
        }

        var window = FindAttachedConsoleWindow(out var isWindowsTerminal);
        if (window == IntPtr.Zero || (isWindowsTerminal &&
            (!HasSingleTerminalPane(window) || FindAttachedConsoleWindow(out _) != window)) ||
            Volatile.Read(ref _closed) != 0)
        {
            return false;
        }

        if (IsIconic(window))
        {
            ShowWindowAsync(window, 9); // SW_RESTORE
        }

        return SetForegroundWindow(window) || GetForegroundWindow() == window;
    }

    private IntPtr FindAttachedConsoleWindow(out bool isWindowsTerminal)
    {
        isWindowsTerminal = false;
        lock (ConsoleAttachment)
        {
            if (FindProcessStart(_claudePid) != _processStart || GetConsoleWindow() != IntPtr.Zero ||
                !AttachConsole((uint)_claudePid))
            {
                return IntPtr.Zero;
            }

            try
            {
                var window = GetConsoleWindow();
                if (HasVisibleWindowClass(window, "ConsoleWindowClass"))
                {
                    return window;
                }

                // Terminal can give the hidden ConPTY window a real top-level owner.
                var owner = window == IntPtr.Zero ? IntPtr.Zero : GetAncestor(window, RootOwner);
                isWindowsTerminal = owner != window &&
                    HasVisibleWindowClass(owner, "CASCADIA_HOSTING_WINDOW_CLASS");
                return isWindowsTerminal ? owner : IntPtr.Zero;
            }
            finally
            {
                FreeConsole();
            }
        }
    }

    private static bool HasVisibleWindowClass(IntPtr window, string expected)
    {
        if (window == IntPtr.Zero || !IsWindowVisible(window))
        {
            return false;
        }

        var windowClass = new StringBuilder(64);
        return GetClassName(window, windowClass, windowClass.Capacity) > 0 &&
            windowClass.ToString() == expected;
    }

    private static bool HasSingleTerminalPane(IntPtr window)
    {
        try
        {
            var terminal = AutomationElement.FromHandle(window);
            var tabs = terminal.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, "TabView"));
            return tabs is not null && tabs.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem)).Count == 1 &&
                terminal.FindAll(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ClassNameProperty, "TermControl")).Count == 1;
        }
        catch (Exception error) when (error is ElementNotAvailableException or COMException or InvalidOperationException
            or ArgumentException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal void Refresh()
    {
        var current = Volatile.Read(ref _snapshot);
        var state = ReadState(_statePath);
        var next = current with { Activity = "Unknown" };
        if (state is not null && state.MatchesProcess(_claudePid, _processStart) &&
            (_pendingSessionId is null || state.SessionId == _pendingSessionId))
        {
            _pendingSessionId = null;
            next = new(state.SessionId ?? current.Id, state.Activity, state.IsSubagent);
        }
        if (next != current)
        {
            Volatile.Write(ref _snapshot, next);
        }
    }

    internal void MarkClosed()
    {
        Volatile.Write(ref _closed, 1);
        Volatile.Write(ref _snapshot, new(SessionId, "Unknown"));
    }

    private static string SessionDirectory()
    {
        var config = WorkingFolder.Normalize(ProfileDataPaths.Config) ?? throw new IOException("Invalid Claude configuration directory.");
        return Path.Combine(config, "sessions");
    }

    private static long? FindProcessStart(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.ProcessName.Equals("claude", StringComparison.OrdinalIgnoreCase) && !process.HasExited
                ? process.StartTime.ToUniversalTime().ToFileTimeUtc()
                : null;
        }
        catch (Exception error) when (error is ArgumentException or Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    private static RuntimeState? ReadState(string path)
    {
        try
        {
            using var state = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (state.Length is <= 0 or > 16 * 1024)
            {
                return null;
            }

            using var document = JsonDocument.Parse(state);
            var row = document.RootElement;
            if (row.ValueKind == JsonValueKind.Object && row.TryGetProperty("pid", out var pid) &&
                pid.ValueKind == JsonValueKind.Number && pid.TryGetInt32(out var process) && process > 0 &&
                row.TryGetProperty("startedAt", out var startedAt) && startedAt.ValueKind == JsonValueKind.Number &&
                startedAt.TryGetInt64(out var timestamp))
            {
                var id = row.TryGetProperty("sessionId", out var session) && session.ValueKind == JsonValueKind.String &&
                    session.TryGetGuid(out var parsed) && parsed != Guid.Empty ? parsed : (Guid?)null;
                var fileTime = GetString(row, "procStartFt");
                var token = fileTime ?? GetString(row, "procStart");
                var activity = (GetString(row, "status"), GetString(row, "waitingFor")) switch
                {
                    ("busy", _) => "Working",
                    ("waiting", "permission prompt" or "sandbox request") => "Approval",
                    ("waiting", "input needed") => "Answer",
                    ("idle" or "waiting", _) => "Waiting",
                    _ => "Unknown",
                };
                return new(process, timestamp, token, fileTime is not null, id, GetString(row, "cwd"), GetString(row, "kind"),
                    GetString(row, "entrypoint"), activity, SessionRole.IsSubagent(row));
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException
            or InvalidOperationException)
        {
            // The CLI creates, replaces, and removes its own status file while it runs.
        }

        return null;
    }

    private static string? GetString(JsonElement row, string field) =>
        row.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private sealed record RuntimeState(int Pid, long StartedAt, string? StartToken, bool ExplicitFileTime, Guid? SessionId, string? Cwd,
        string? Kind, string? EntryPoint, string Activity, bool IsSubagent)
    {
        public bool MatchesProcess(int pid, long processStart)
        {
            if (pid != Pid)
            {
                return false;
            }

            if (StartToken is not null)
            {
                if (!long.TryParse(StartToken, NumberStyles.None, CultureInfo.InvariantCulture, out var token))
                {
                    return false;
                }

                if (token == processStart || ExplicitFileTime)
                {
                    return token == processStart;
                }

                // Older Windows registrations used WMI CreationDate ticks at microsecond precision.
                var started = DateTime.FromFileTimeUtc(processStart);
                return token / 10 == started.Ticks / 10 || token / 10 == started.ToLocalTime().Ticks / 10;
            }

            var startedAt = new DateTimeOffset(DateTime.FromFileTimeUtc(processStart)).ToUnixTimeMilliseconds();
            return StartedAt >= startedAt && StartedAt - startedAt <= 120_000;
        }
    }

    private sealed record Snapshot(Guid? Id, string Activity, bool IsSubagent = false);

    [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetClassName(IntPtr window, StringBuilder className, int capacity);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr window, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindowAsync(IntPtr window, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(uint processId);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeConsole();
}
