using System.Globalization;
using System.Security;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;

namespace ClaudeLauncher;

public partial class SetupPanel
{
    private int _historyRequest;
    private sealed record RepairRow(Guid Id, string Action, string Title, string Detail, bool CanUndo);

    private async void History_Expanded(object sender, RoutedEventArgs e) => await LoadRepairHistoryAsync();

    private async Task LoadRepairHistoryAsync()
    {
        var request = ++_historyRequest;
        HistoryRows.ItemsSource = null;
        HistoryNotice.Visibility = Visibility.Visible;
        HistoryNotice.Text = UiLanguage.Text("HistoryReading");
        try
        {
            var entries = await Task.Run(RepairJournal.List);
            if (request != _historyRequest)
                return;
            var culture = CultureInfo.GetCultureInfo(UiLanguage.Current);
            var rows = entries.OrderByDescending(entry => entry.At).Select(entry =>
            {
                var title = UiLanguage.TryText("HistoryAction" + entry.Action) ?? UiLanguage.TryText("Fix" + entry.Action)
                    ?? UiLanguage.Text("RecordedRepair");
                if (entry.IsUndo)
                    title = Format("HistoryUndoAction", title);
                var status = UiLanguage.TryText("History" + entry.Status) ?? UiLanguage.Text("HistoryUnknown");
                var detail = entry.At == DateTimeOffset.MinValue ? status : entry.At.ToLocalTime().ToString("g", culture) + "  ·  " + status;
                if (!string.IsNullOrEmpty(entry.Subject))
                    detail += "  ·  " + entry.Subject;
                if (!entry.CanUndo && !entry.IsUndo && entry.Status == "Applied")
                    detail += "  ·  " + UiLanguage.Text("HistoryOnly");
                return new RepairRow(entry.Id, entry.Action, title, detail, entry.CanUndo);
            }).ToArray();
            HistoryRows.ItemsSource = rows;
            HistoryNotice.Text = UiLanguage.Text("HistoryEmptyList");
            HistoryNotice.Visibility = Show(rows.Length == 0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException
            or CryptographicException or System.Text.Json.JsonException or InvalidOperationException)
        {
            if (request == _historyRequest)
                HistoryNotice.Text = UiLanguage.Text("HistoryReadFailed");
        }
    }

    private void UndoRepair_Click(object sender, RoutedEventArgs e)
    {
        if (IsBusy || _approval is not null || sender is not Button { DataContext: RepairRow { CanUndo: true } row })
            return;
        var question = row.Action == SettingsDiagnostics.RestoreAction ? "UndoSettingsQuestion" : "UndoRepairQuestion";
        OpenApproval(new(UiLanguage.Text("UndoRepair"), UiLanguage.Text(question),
            row.Title, row.Detail, null, row.Id));
    }

    private void ProtectionHistory_Click(object sender, RoutedEventArgs e)
    {
        if (!DefenderDiagnostics.OpenProtectionHistory())
        {
            _feedback = "ProtectionHistoryFailed";
            Render();
        }
    }
}
