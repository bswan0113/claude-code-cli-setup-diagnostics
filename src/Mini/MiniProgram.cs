using System.ComponentModel;
using System.Runtime.InteropServices;
using static ClaudeLauncher.MiniNative;

namespace ClaudeLauncher;

internal static class MiniProgram
{
    [STAThread]
    private static int Main()
    {
        try
        {
            UiLanguage.Apply(UiLanguage.GetInitialLanguage());
            return new MiniWindow().Run();
        }
        catch (Exception error)
        {
            MessageBoxW(0, error.Message, "Claude Code · Mini", 0x10);
            return 1;
        }
    }
}

// The window owns HWNDs and the message loop. The controller owns every diagnostic and repair decision.
internal sealed class MiniWindow
{
    private enum Page { Summary, Findings, History }
    private enum Id { Language = 101, Heading, Items, Description, Details, History, More, Primary, Secondary }
    private readonly MiniDiagnostics _diagnostics = new(Environment.SystemDirectory);
    private readonly Dictionary<Id, nint> _controls = new();
    private readonly nint _instance = GetModuleHandleW(null);
    private static readonly string[] Languages = ["en", "ko", "zh-Hans", "ja"];
    private readonly List<(string Caption, DiagnosticItemView? Finding, MiniHistoryRow? Entry)> _rows = [];
    private readonly WindowProc _proc;
    private Page _page;
    private MiniState? _shown;
    private bool _closing, _ready, _urgent;
    private int _selection;
    private nint _window, _font, _headingFont;
    private uint _dpi = 96;
    private const string WindowClassName = "BlsLab.ClaudeRepairMini";

    internal MiniWindow() => _proc = OnMessage;

    internal int Run()
    {
        var cls = new WindowClass { name = WindowClassName, instance = _instance, procedure = _proc,
            background = GetSysColorBrush(WindowColor), cursor = LoadCursorW(0, 32512),
            icon = LoadIconW(_instance, 32512) };
        Checked(RegisterClassW(ref cls));
        _dpi = GetDpiForSystem();
        SystemParametersInfoW(WorkArea, 0, out var area, 0);
        int w = Math.Min(Pixels(560), area.right - area.left), h = Math.Min(Pixels(420), area.bottom - area.top);
        _window = Checked(CreateWindowExW(0, WindowClassName, "Claude Code · Mini", 0x00CF0000,
            area.left + (area.right - area.left - w) / 2, area.top + (area.bottom - area.top - h) / 2,
            w, h, 0, 0, _instance, 0));
        Add(Id.Heading, "STATIC", 0x200);
        Add(Id.Language, "COMBOBOX", TabStop | VScroll | 3);
        foreach (var name in new[] { "English", "한국어", "简体中文", "日本語" })
            SendText(_controls[Id.Language], ComboAdd, 0, name);
        Select(Id.Language, Math.Max(0, Array.IndexOf(Languages, UiLanguage.Current)));
        Add(Id.Items, "COMBOBOX", TabStop | VScroll | 3);
        Add(Id.Description, "EDIT", TabStop | VScroll | 0x0804); // read-only multiline
        foreach (var id in new[] { Id.Details, Id.History, Id.More, Id.Primary, Id.Secondary })
            Add(id, "BUTTON", TabStop | 0x2000); // multiline labels for longer translations
        _ready = true;
        UpdateFonts();
        _diagnostics.Changed += OnChanged;
        Render();
        ShowWindow(_window, 5);
        SetFocus(_controls[Id.Description]);
        PostMessageW(_window, StartScan, 0, 0);
        int result;
        while ((result = GetMessageW(out var msg, 0, 0, 0)) > 0)
        {
            if (IsDialogMessageW(_window, ref msg)) continue;
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }
        GC.KeepAlive(_proc);
        return result == -1 ? 1 : 0;
    }

    private void OnChanged() => PostMessageW(_window, Changed, 0, 0);
    private void Add(Id id, string kind, uint flags) =>
        _controls.Add(id, Checked(CreateWindowExW(0, kind, "", Child | Visible | flags, 0, 0, 0, 0, _window, (int)id, _instance, 0)));
    private static string T(string key) => UiLanguage.Text(key);
    private int Pixels(int dip) => (int)Math.Round(dip * _dpi / 96d);
    private void Select(Id id, int index) => SendMessageW(_controls[id], ComboSelect, index, 0);
    private int Selected(Id id) => (int)SendMessageW(_controls[id], ComboGet, 0, 0);
    private void Label(Id id, string text) => SetWindowTextW(_controls[id], text);
    private void Display(Id id, bool show) => ShowWindow(_controls[id], show ? 5 : 0);
    private void Enable(Id id, bool enabled) => EnableWindow(_controls[id], enabled);
    private void Move(Id id, int x, int y, int w, int h) =>
        MoveWindow(_controls[id], Pixels(x), Pixels(y), Math.Max(1, Pixels(w)), Math.Max(1, Pixels(h)), true);

    private void UpdateFonts()
    {
        _dpi = GetDpiForWindow(_window);
        nint MakeFont(int size, int weight) => CreateFontW(-Pixels(size), 0, 0, 0, weight, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
        var old = _font;
        var oldHeading = _headingFont;
        _font = MakeFont(15, 400);
        _headingFont = MakeFont(20, 600);
        foreach (var (id, handle) in _controls)
            SendMessageW(handle, SetFont, id == Id.Heading ? _headingFont : _font, 1);
        if (old != 0) DeleteObject(old);
        if (oldHeading != 0) DeleteObject(oldHeading);
    }

    private nint OnMessage(nint hwnd, uint msg, nint wp, nint lp)
    {
        switch (msg)
        {
            case ActionError:
                MessageBoxW(hwnd, T("FullCheckFailed"), "Claude Code · Mini", 0x10); return 0;
            case StartScan: RunCommand(_diagnostics.ScanAsync); return 0;
            case Changed:
                if (_closing && _diagnostics.RequestClose()) DestroyWindow(hwnd);
                else Render();
                return 0;
            case Command when _ready:
                var id = (int)(wp & 0xffff);
                if (id == 2 && lp == 0) { Secondary(); return 0; } // Escape
                if (lp != 0) CommandClick((Id)id, (int)((wp >> 16) & 0xffff));
                return 0;
            case SizeChanged when _ready: Layout(); return 0;
            case GetMinMaxInfo:
                var limits = Marshal.PtrToStructure<MinMax>(lp);
                limits.minTrack = new Point { x = Pixels(360), y = Pixels(340) };
                Marshal.StructureToPtr(limits, lp, false);
                return 0;
            case DpiChanged when _ready:
                var rect = Marshal.PtrToStructure<Rect>(lp);
                MoveWindow(hwnd, rect.left, rect.top, rect.right - rect.left, rect.bottom - rect.top, true);
                UpdateFonts(); Layout(); return 0;
            case ControlColor when _ready && lp == _controls[Id.Heading]:
                if (_urgent) SetTextColor(wp, 0x002323ad); // COLORREF red
                SetBkMode(wp, 1);
                return GetSysColorBrush(WindowColor);
            case Close when _ready:
                _closing = true;
                if (_diagnostics.RequestClose()) DestroyWindow(hwnd);
                else { SetWindowTextW(hwnd, T("ClosingAfterWork")); Render(); }
                return 0;
            case Destroy:
                _ready = false;
                _diagnostics.Changed -= OnChanged;
                if (_font != 0) DeleteObject(_font);
                if (_headingFont != 0) DeleteObject(_headingFont);
                PostQuitMessage(0); return 0;
        }
        return DefWindowProcW(hwnd, msg, wp, lp);
    }

    private async void RunCommand(Func<Task> operation)
    {
        try { await operation().ConfigureAwait(false); }
        catch (Exception) { PostMessageW(_window, ActionError, 0, 0); }
    }
    private const uint ActionError = 0x8003;

    private void Layout()
    {
        GetClientRect(_window, out var rc);
        int width = (int)(rc.right * 96d / _dpi), height = (int)(rc.bottom * 96d / _dpi);
        const int pad = 14, gap = 6, buttonHeight = 36;
        int content = width - 2 * pad;
        Move(Id.Heading, pad, pad, content - 120, 30);
        Move(Id.Language, width - pad - 114, pad, 114, 190);
        int top = pad + 40;
        bool list = _page != Page.Summary && _shown?.Approval == null;
        if (list) { Move(Id.Items, pad, top, content, 260); top += 34; }
        int footer = height - pad - buttonHeight;
        int navigation = footer - gap - buttonHeight;
        Move(Id.Description, pad, top, content, navigation - top - gap);
        int navWidth = (content - 2 * gap) / 3;
        Move(Id.Details, pad, navigation, navWidth, buttonHeight);
        Move(Id.History, pad + navWidth + gap, navigation, navWidth, buttonHeight);
        Move(Id.More, pad + 2 * (navWidth + gap), navigation, content - 2 * (navWidth + gap), buttonHeight);
        int side = Math.Min(160, content / 2);
        Move(Id.Primary, pad, footer, content - side - gap, buttonHeight);
        Move(Id.Secondary, width - pad - side, footer, side, buttonHeight);
    }

    private void Render()
    {
        var state = _shown = _diagnostics.State;
        _urgent = !state.Busy && state.StatusKey == "GuidedIssue" && _page == Page.Summary;
        Label(Id.Heading, state.Busy ? state.Work : state.Approval is not null ? T("ApprovalTitle") :
            _page == Page.Findings ? T("GuidedDetails") : _page == Page.History ? T("RepairHistoryHeading") :
            state.Status.Length > 0 ? state.Status : T("InstallRepairTitle"));
        Label(Id.Details, T(_page == Page.Findings ? "FullBackToQuick" : "GuidedDetails"));
        Label(Id.History, T(_page == Page.History ? "FullBackToQuick" : "RepairHistoryHeading"));
        Label(Id.More, T("MiniMoreActions"));
        bool browse = !state.Busy && state.Approval is null && !_closing;
        Enable(Id.Language, browse);
        Enable(Id.Details, browse && state.Stages.Count != 0);
        Enable(Id.History, browse);
        Enable(Id.More, browse);
        bool list = _page != Page.Summary && state.Approval is null;
        Display(Id.Items, list);
        if (list)
        {
            _rows.Clear();
            if (_page == Page.Findings)
            {
                if (state.Evidence.Length > 0) _rows.Add((state.Status + " · " + state.Title, null, null));
                foreach (var stage in state.Stages)
                {
                    if (stage.IsEmpty) _rows.Add((stage.Title + " · " + T("FullStageNoResults"), null, null));
                    foreach (var item in stage.Items)
                        _rows.Add(($"{stage.Title} · {item.Status} · {item.Title}", item, null));
                }
            }
            else foreach (var entry in state.History) _rows.Add((entry.Title + " · " + entry.Detail, null, entry));
            var box = _controls[Id.Items];
            SendMessageW(box, ComboReset, 0, 0);
            foreach (var row in _rows) SendText(box, ComboAdd, 0, row.Caption);
            _selection = _rows.Count == 0 ? -1 : Math.Clamp(_selection, 0, _rows.Count - 1);
            Select(Id.Items, _selection);
            Enable(Id.Items, browse && _rows.Count > 0);
        }
        RenderBody();
        Layout();
    }

    private DiagnosticItemView? Finding => _selection >= 0 && _selection < _rows.Count ? _rows[_selection].Finding : null;
    private MiniHistoryRow? HistoryEntry => _selection >= 0 && _selection < _rows.Count ? _rows[_selection].Entry : null;
    private static string Section(string key, string value) => string.IsNullOrWhiteSpace(value) ? "" : T(key) + "\n" + value;
    private static string Paragraphs(params string?[] sections) =>
        string.Join("\n\n", sections.Where(text => !string.IsNullOrWhiteSpace(text))).ReplaceLineEndings("\r\n");

    private void RenderBody()
    {
        if (_shown is not { } state) return;
        string body, primary = "", secondary = T("CloseSetup");
        if (state.Busy)
        {
            body = state.Work;
            if (state.CanCancel) secondary = T("CancelSetup");
        }
        else if (state.Approval is { } approval)
        {
            body = Paragraphs(approval.Title, approval.Description, Section("FullChangeTarget", approval.Target),
                Section("FullEvidence", approval.Evidence));
            primary = T("ApprovalApply"); secondary = T("CancelSetup");
        }
        else if (_page == Page.Findings)
        {
            body = Finding is { } finding ? Paragraphs(finding.Title, Section("FullCause", finding.Cause),
                Section("FullSolution", finding.Solution), Section("FullEvidence", finding.Evidence),
                Section(finding.Check.Action is null ? "FullObservedTarget" : "FullChangeTarget", finding.Target)) :
                _selection == 0 && state.Evidence.Length > 0 ? Section("FullEvidence", state.Evidence) : T("FullStageNoResults");
            if (Finding is { CanReview: true } action) primary = action.ActionLabel ?? "";
        }
        else if (_page == Page.History)
        {
            body = HistoryEntry is { } entry ? Paragraphs(entry.Title, entry.Detail) : state.HistoryNotice;
            if (HistoryEntry?.CanUndo == true) primary = T("UndoRepair");
        }
        else
        {
            body = Paragraphs(state.Title, state.Body, Section("FullChangeTarget", state.Target), state.Defender);
            primary = state.RecommendedLabel;
            if (primary.Length > 0) secondary = T("GuidedNo");
        }
        Label(Id.Description, Paragraphs(state.Feedback, body));
        Label(Id.Primary, primary);
        Display(Id.Primary, primary.Length > 0);
        Enable(Id.Primary, !state.Busy && !_closing);
        Label(Id.Secondary, secondary);
        Enable(Id.Secondary, !_closing && (!state.Busy || state.CanCancel));
    }

    private void Secondary()
    {
        if (_closing || _shown is not { } state) return;
        if (state.Busy && state.CanCancel) _diagnostics.CancelReadOrLogin();
        else if (state.Approval is not null) _diagnostics.Decline();
        else PostMessageW(_window, Close, 0, 0);
    }

    private void CommandClick(Id id, int notification)
    {
        if (_closing || _shown is not { } state) return;
        if (id == Id.Items && notification == 1)
        {
            _selection = Selected(Id.Items);
            RenderBody();
            return;
        }
        if (id == Id.Language && notification == 1 && !state.Busy && state.Approval is null)
        {
            var index = Selected(Id.Language);
            if ((uint)index >= Languages.Length) return;
            UiLanguage.Apply(Languages[index]);
            UiLanguage.Save();
            Render();
            return;
        }
        if (notification != 0) return;
        if (id == Id.Secondary) { Secondary(); return; }
        if (state.Busy) return;
        if (state.Approval is { } approved)
        {
            if (id == Id.Primary) RunCommand(() => _diagnostics.ConfirmAsync(approved.Id));
            return;
        }
        switch (id)
        {
            case Id.Primary when _page == Page.Summary && state.RecommendedLabel.Length > 0:
                RunCommand(() => _diagnostics.RecommendAsync(state.ScanId)); break;
            case Id.Primary when _page == Page.Findings && Finding is { CanReview: true } finding:
                RunCommand(() => _diagnostics.ReviewAsync(finding.Check)); break;
            case Id.Primary when _page == Page.History && HistoryEntry is { CanUndo: true } row:
                RunCommand(() => _diagnostics.ReviewUndoAsync(row.Id)); break;
            case Id.Details: Navigate(Page.Findings); break;
            case Id.History: Navigate(Page.History); break;
            case Id.More: ShowMenu(state); break;
        }
    }

    private void Navigate(Page next)
    {
        _page = _page == next ? Page.Summary : next;
        _selection = 0;
        Render();
        if (_page == Page.History) RunCommand(_diagnostics.LoadHistoryAsync);
        else if (_page == Page.Findings) SetFocus(_controls[Id.Items]);
    }

    private void ShowMenu(MiniState state)
    {
        var menu = Checked(CreatePopupMenu());
        int selection;
        try
        {
            AppendMenuW(menu, 0, 1, T("CheckAgain"));
            AppendMenuW(menu, state.ScanId == Guid.Empty ? 1u : 0, 2, T("CopySummary"));
            AppendMenuW(menu, 0, 3, T("OfficialHelp"));
            if (state.Defender.Length != 0) AppendMenuW(menu, 0, 4, T("OpenProtectionHistory"));
            GetWindowRect(_controls[Id.More], out var pos);
            selection = TrackPopupMenu(menu, 0x0100 | 0x0002, pos.left, pos.bottom, 0, _window, 0);
        }
        finally { DestroyMenu(menu); }
        switch (selection)
        {
            case 1: _page = Page.Summary; RunCommand(_diagnostics.ScanAsync); break;
            case 2: _diagnostics.ReportCopy(Copy(_window, _diagnostics.SummaryText())); break;
            case 3: _diagnostics.OpenHelp(); break;
            case 4: _diagnostics.OpenProtectionHistory(); break;
        }
    }
}
