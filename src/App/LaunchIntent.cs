namespace ClaudeLauncher;

internal enum LaunchMode { New, Resume, Picker }

internal sealed record LaunchIntent(string Folder, LaunchMode Mode = LaunchMode.New, Guid? SessionId = null);

internal sealed record DiagnosticNotice(string Code, string? Detail = null,
    bool AllowRepair = false, int? ExitCode = null);
