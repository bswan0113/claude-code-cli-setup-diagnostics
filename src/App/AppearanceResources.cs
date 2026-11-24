using System.Windows;

namespace ClaudeLauncher;

internal static class AppearanceResources
{
    private static readonly int[] FontSizes = [12, 13, 14, 15, 16, 18, 19, 21, 22, 23, 25, 28];
    private static ResourceDictionary? _palette;
    private static ResourceDictionary? _fonts;
    private static bool? _darkMode;
    private static int _fontPercent;

    public static void Apply(AppearancePreferences preferences)
    {
        var dictionaries = Application.Current.Resources.MergedDictionaries;
        if (_darkMode != preferences.DarkMode)
        {
            _darkMode = preferences.DarkMode;
            var name = preferences.DarkMode ? "Dark" : "Light";
            Replace(dictionaries, ref _palette, new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/App/Resources/Palette.{name}.xaml")
            });
        }

        if (_fontPercent != preferences.FontPercent)
        {
            _fontPercent = preferences.FontPercent;
            var scaled = new ResourceDictionary();
            foreach (var size in FontSizes)
                scaled[$"Font{size}"] = size * _fontPercent / 100.0;

            Replace(dictionaries, ref _fonts, scaled);
        }
    }

    private static void Replace(ICollection<ResourceDictionary> dictionaries, ref ResourceDictionary? existing,
        ResourceDictionary next)
    {
        dictionaries.Add(next);
        if (existing is not null)
            dictionaries.Remove(existing);
        existing = next;
    }
}
