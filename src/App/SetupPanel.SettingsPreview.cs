using System.Security;

namespace ClaudeLauncher;

public partial class SetupPanel
{
    private async Task ReviewSettingsBackupAsync(CheckItem item)
    {
        if (IsBusy || _approval is not null || item.Action != SettingsDiagnostics.RestoreAction)
            return;

        SettingsDiagnostics.Preview? preview = null;
        Begin("SettingsPreparingPreview");
        try
        {
            preview = await SettingsDiagnostics.PreviewAsync(item, CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or InvalidOperationException)
        {
            // Keep raw parser errors, paths from malformed documents and config values out of UI logs.
        }
        finally { End(); }
        if (preview is null)
        {
            await CheckAsync(null, false);
            _feedback = "SettingsPreviewChanged";
            Render();
            return;
        }
        var scope = UiLanguage.Text(preview.ScopeKey);
        if (preview.KnownSection is { } section)
            scope += Environment.NewLine + Format("SettingsChangedSection", section);
        var description = scope + Environment.NewLine + UiLanguage.Text("SettingsDiffRedacted");
        var paths = item.Target!.Split(Environment.NewLine, StringSplitOptions.None);
        var target = Format("SettingsFileTarget", paths[0]);
        if (paths.Length > 1)
            target += Environment.NewLine + Format("SettingsBackupSource", paths[1]);
        target += Environment.NewLine + Format("SettingsKeepOriginal", preview.RecoveryPath);
        OpenApproval(new RepairApproval(UiLanguage.Text("FixRestoreSettingsBackup"), description,
            target, preview.Difference, item));
    }
}
