using System.Globalization;

namespace ClaudeLauncher;

internal sealed record UsageText(string Line, string Detail)
{
    private static CultureInfo Culture => CultureInfo.GetCultureInfo(UiLanguage.Current);
    private static string Format(string key, params object[] values) =>
        string.Format(Culture, UiLanguage.Text(key), values);

    internal static string Compact(decimal value, bool lowerBound = false)
    {
        if (value < 1_000)
            return value.ToString("0", Culture);
        var (divisor, suffix) = value > 999_990m ? (1_000_000m, "M") : (1_000m, "K");
        var scaled = lowerBound ? decimal.Truncate(value / divisor * 10) / 10
            : decimal.Round(value / divisor, 1, MidpointRounding.AwayFromZero);
        if (suffix == "K")
            scaled = Math.Min(scaled, 999.9m);
        return scaled.ToString("0.0", Culture) + suffix;
    }

    public static UsageText Session(SessionUsage? usage, bool loading)
    {
        if (usage is null && loading)
            return Pending();
        var partial = usage?.IsPartial == true;
        var total = usage?.TotalTokens;
        var context = usage?.ContextTokens;
        var percent = usage?.ContextPercent is double value && double.IsFinite(value)
            ? (usage.IsWindowEstimated ? "≈" : "") + value.ToString("0.#", Culture) + "%"
            : "—%";
        var contextLabel = context is null && usage?.ContextPercent is null
            ? UiLanguage.Text("UsageContextUnknown")
            : Format("UsageContext", Short(context, false), percent);
        var line = Format("UsageTotal", Short(total, partial)) + "  ·  " + contextLabel;
        var detail = Format("UsageTotal", Exact(total)) + Environment.NewLine +
            Format("UsageContextDetail", Exact(context), Exact(usage?.ContextWindowTokens), percent);
        if (usage?.IsWindowEstimated == true)
            detail += Environment.NewLine + UiLanguage.Text("UsageEstimatedWindow");
        return Finish(line, detail, usage?.Compactions, partial);
    }

    public static UsageText Folder(IReadOnlyList<SavedSession> sessions, bool unreadable)
    {
        if (sessions.Count > 0 && sessions.All(session => session.Usage is null))
            return Pending();
        decimal total = 0;
        var measured = 0;
        var partial = unreadable;
        foreach (var session in sessions)
        {
            if (session.Usage?.TotalTokens is long value)
            {
                total += value;
                measured++;
            }
            partial |= session.Usage?.IsPartial == true;
        }
        partial |= measured < sessions.Count;
        decimal? known = measured > 0 || (sessions.Count == 0 && !unreadable) ? total : null;
        var line = Format("UsageTotal", Short(known, partial));
        var detail = Format("UsageFolderDetail", Exact(known), measured, sessions.Count);
        return Finish(line, detail, null, partial);
    }

    private static UsageText Pending()
    {
        var message = UiLanguage.Text("UsageLoading");
        return new(message, message);
    }

    private static string Short(decimal? value, bool partial) =>
        value.HasValue ? (partial ? "≥" : "") + Compact(value.Value, partial) : "—";

    private static string Exact(decimal? value) =>
        value?.ToString("N0", Culture) ?? UiLanguage.Text("UsageUnknown");

    private static UsageText Finish(string line, string detail, long? compactions, bool partial)
    {
        if (compactions > 0)
        {
            var count = Format("UsageCompactions", compactions.Value.ToString("N0", Culture));
            line += "  ·  " + count;
            detail += Environment.NewLine + count;
        }
        if (partial)
            detail += Environment.NewLine + UiLanguage.Text("UsagePartial");
        return new(line, detail);
    }
}
