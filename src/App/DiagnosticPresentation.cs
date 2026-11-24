using System.Globalization;

namespace ClaudeLauncher;

internal sealed class DiagnosticItemView(CheckItem item)
{
    internal CheckItem Check { get; } = item;
    public CheckState State => Check.State;
    public string Status => StateText(State);
    public string Title => UiLanguage.TryText("Check" + Check.Key + "Title") ?? Check.Key;
    public string Cause => UiLanguage.TryText("Check" + Check.Key + "Cause") ?? UiLanguage.Text("FullCauseUnknown");
    public string Solution => UiLanguage.TryText("Check" + Check.Key + "Solution") ?? UiLanguage.Text("FullSolutionUnknown");
    public string Evidence => Check.Evidence;
    public string Target => Check.Target ?? "";
    public string TargetLabel => UiLanguage.Text(Check.Action is null ? "FullObservedTarget" : "FullChangeTarget");
    public bool HasEvidence => !string.IsNullOrWhiteSpace(Evidence);
    public bool HasTarget => !string.IsNullOrWhiteSpace(Target);
    public string? ActionLabel => Check.Action is { } action ? UiLanguage.TryText("Fix" + action) : null;
    public bool CanReview => HasTarget && !string.IsNullOrWhiteSpace(ActionLabel);
    public string ActionName => $"{ActionLabel}, {Title}";

    internal static string StateText(CheckState state) =>
        UiLanguage.TryText("CheckState" + state) ?? UiLanguage.Text("CheckStateUnknown");
}

internal sealed class DiagnosticStageView(int stage, IReadOnlyList<DiagnosticItemView> items, bool expanded)
{
    public int Stage { get; } = stage;
    public IReadOnlyList<DiagnosticItemView> Items { get; } = items;
    public bool Expanded { get; set; } = expanded;
    public bool IsEmpty => Items.Count == 0;
    public string Title => $"{Stage}. {UiLanguage.Text("CheckStage" + Stage)}";

    public CheckState State => Items.Count == 0 ? CheckState.Unknown : Items.Select(item => item.State).MaxBy(Priority);
    public string Status => string.Format(CultureInfo.GetCultureInfo(UiLanguage.Current),
        UiLanguage.Text("FullStageStatus"), DiagnosticItemView.StateText(State), Items.Count);
    public string AutomationName => $"{Title}, {Status}";

    private static int Priority(CheckState state) => state switch
    {
        CheckState.Failed => 4,
        CheckState.Warning => 3,
        CheckState.Unknown => 2,
        CheckState.Info => 1,
        _ => 0
    };
}
