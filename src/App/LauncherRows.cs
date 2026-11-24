using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace ClaudeLauncher;

internal abstract class LauncherRow(UsageText? usage = null) : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public UsageText? Usage { get; private set; } = usage;

    internal void SetUsage(UsageText updated)
    {
        if (Usage == updated)
            return;
        Usage = updated;
        Changed(nameof(Usage));
    }

    protected void Changed([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

internal sealed class LiveRow(LiveTerminal terminal, int ordinal, bool owned) : LauncherRow
{
    public LiveTerminal Terminal { get; private set; } = terminal;
    public int Ordinal { get; } = ordinal;
    public bool Owned { get; } = owned;
    public Guid? SessionId { get; private set; }
    public string Title { get; private set; } = "";
    public string Subtitle { get; private set; } = "";
    public string Activity { get; private set; } = "";
    private SessionUsage? _lastUsage;
    private bool _usagePending;
    private string? _usageLanguage;
    public string AutomationName =>
        $"{Title}, {UiLanguage.Text("FocusTerminalPlain")}, {Terminal.Folder}, {ActivityLabel}";
    public string ActivityLabel => UiLanguage.Text(Activity switch
    {
        "Starting" => "ActivityStarting",
        "Working" => "ActivityWorking",
        "Waiting" => "ActivityWaiting",
        "Approval" => "ActivityApproval",
        "Answer" => "ActivityAnswer",
        _ => "ActivityUnknown"
    });

    public void UseTerminal(LiveTerminal snapshot) => Terminal = snapshot;

    public void Update(Guid? id, string title, string folder, string activity, SavedSession? saved = null)
    {
        var pending = saved is { Usage: null };
        if (_lastUsage != saved?.Usage || _usagePending != pending || _usageLanguage != UiLanguage.Current)
        {
            _lastUsage = saved?.Usage;
            _usagePending = pending;
            _usageLanguage = UiLanguage.Current;
            SetUsage(UsageText.Session(_lastUsage, pending));
        }
        if (SessionId != id)
        {
            SessionId = id;
            Changed(nameof(SessionId));
        }

        if (Title != title)
        {
            Title = title;
            Changed(nameof(Title));
        }

        var subtitle = Owned
            ? string.Format(CultureInfo.GetCultureInfo(UiLanguage.Current), UiLanguage.Text("RunningMetadata"),
                FolderName(folder), Ordinal)
            : string.Format(CultureInfo.GetCultureInfo(UiLanguage.Current), UiLanguage.Text("ExternalTerminalMetadata"),
                FolderName(folder));
        if (Subtitle != subtitle)
        {
            Subtitle = subtitle;
            Changed(nameof(Subtitle));
        }

        if (Activity != activity)
        {
            Activity = activity;
            Changed(nameof(Activity));
        }
        Changed(nameof(ActivityLabel));
        Changed(nameof(AutomationName));
    }

    public static string FolderName(string path)
    {
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        return string.IsNullOrEmpty(name) ? path : name;
    }
}

internal abstract class TreeRow(string key, bool expanded = false, UsageText? usage = null) : LauncherRow(usage)
{
    private bool _isSelected;
    private bool _isExpanded = expanded;
    public string Key { get; } = key;
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
                return;
            _isSelected = value;
            Changed();
        }
    }
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value)
                return;
            _isExpanded = value;
            Changed();
        }
    }
}

internal sealed class FolderTreeRow(string path, IReadOnlyList<TreeRow> children, IReadOnlyList<SavedSession> sessions,
    bool unreadable, bool expanded)
    : TreeRow("folder:" + path, expanded, UsageText.Folder(sessions, unreadable))
{
    public string Path { get; } = path;
    public string Title { get; } = LiveRow.FolderName(path);
    public IReadOnlyList<TreeRow> Children { get; } = children;
    public string CountLabel => string.Format(CultureInfo.GetCultureInfo(UiLanguage.Current),
        UiLanguage.Text("FolderSessionCount"), sessions.Count);
}

internal sealed class SessionTreeRow(SavedSession session, DateTime lastUsedUtc, LiveRow? running)
    : TreeRow("session:" + session.Id.ToString("N"), usage: UsageText.Session(session.Usage, loading: true))
{
    public SavedSession Session { get; } = session;
    public LiveRow? Running { get; } = running;
    public bool IsRunning => Running is not null;
    public string Subtitle => $"{LiveRow.FolderName(Session.Cwd)}  ·  {When}";
    public string When
    {
        get
        {
            var culture = CultureInfo.GetCultureInfo(UiLanguage.Current);
            var elapsed = DateTime.UtcNow - lastUsedUtc;
            if (elapsed.TotalMinutes < 1)
                return UiLanguage.Text("TimeNow");
            if (elapsed.TotalHours < 1)
                return string.Format(culture, UiLanguage.Text("TimeMinutes"), (int)elapsed.TotalMinutes);
            if (elapsed.TotalDays < 1)
                return string.Format(culture, UiLanguage.Text("TimeHours"), (int)elapsed.TotalHours);
            if (elapsed.TotalDays < 7)
                return string.Format(culture, UiLanguage.Text("TimeDays"), (int)elapsed.TotalDays);
            return lastUsedUtc.ToLocalTime().ToString("d", culture);
        }
    }
    public string FullTime => lastUsedUtc.ToLocalTime().ToString("g", CultureInfo.GetCultureInfo(UiLanguage.Current));
    public string ResumeAutomationId { get; } = $"ResumeSession_{session.Id:N}";
    public string TreeAutomationId { get; } = $"TreeSession_{session.Id:N}";
    public string AutomationName =>
        $"{Session.Title}, {UiLanguage.Text(IsRunning ? "FocusTerminalPlain" : "ResumeSessionPlain")}, {Session.Cwd}";
}

internal sealed class FallbackTreeRow(string path) : TreeRow("fallback:" + path)
{
    public string Path { get; } = path;
}

internal sealed class EmptyTreeRow(string path) : TreeRow("empty:" + path);
