using System.Text.Json;

namespace ClaudeLauncher;

internal enum FolderSortOrder
{
    RecentDescending, RecentAscending, NameAscending, NameDescending, PathAscending, PathDescending
}

internal sealed record AppearancePreferences(bool DarkMode = false, int FontPercent = 100, int OpacityPercent = 100)
{
    public FolderSortOrder FolderOrder { get; init; } = FolderSortOrder.RecentDescending;
    public bool Sidebar { get; init; }
    public bool SidebarRight { get; init; }
    public string? SidebarMonitor { get; init; }
    public double SidebarWidth { get; init; } = 450;
    public double? SidebarHeight { get; init; }

    private static readonly string Store = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ClaudeCodeSetupDiagnostics", "appearance.json");

    public static AppearancePreferences Load()
    {
        try
        {
            using var file = new FileStream(Store, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            return JsonSerializer.Deserialize<AppearancePreferences>(file)?.Normalized() ?? new();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return new();
        }
    }

    private AppearancePreferences Normalized() => this with
    {
        FolderOrder = Enum.IsDefined(FolderOrder) ? FolderOrder : FolderSortOrder.RecentDescending,
        SidebarWidth = double.IsFinite(SidebarWidth) && SidebarWidth > 0 ? Math.Max(300, SidebarWidth) : 450,
        SidebarHeight = SidebarHeight is > 0 and < double.PositiveInfinity ? Math.Max(460, SidebarHeight.Value) : null,
        FontPercent = NearestStep(FontPercent, 85, 125),
        OpacityPercent = NearestStep(OpacityPercent, 60, 100)
    };

    private static int NearestStep(int percent, int minimum, int maximum) =>
        ((Math.Clamp(percent, minimum, maximum) + 2) / 5) * 5;

    public bool Save()
    {
        var temporary = $"{Store}.{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Store)!);
            using (var file = File.Open(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                JsonSerializer.Serialize(file, Normalized());
            File.Move(temporary, Store, overwrite: true);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }
}
