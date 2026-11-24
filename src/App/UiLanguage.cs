using System.Globalization;
using System.IO;
using System.Windows;

namespace ClaudeLauncher;

internal static class UiLanguage
{
    private static readonly string PreferencePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ClaudeCodeSetupDiagnostics", "language.txt");

    private static ResourceDictionary? _translation;
    private static ResourceDictionary? _english;

    public static string Current { get; private set; } = "en";

    public static string GetInitialLanguage()
    {
        try
        {
            var saved = File.ReadAllText(PreferencePath).Trim();
            if (saved is "ko" or "en" or "zh-Hans" or "ja")
                return saved;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }

        var displayLanguage = CultureInfo.CurrentUICulture.Name;
        if (displayLanguage is "zh" or "zh-CN" or "zh-SG" ||
            displayLanguage.StartsWith("zh-Hans", StringComparison.OrdinalIgnoreCase))
        {
            return "zh-Hans";
        }

        return CultureInfo.CurrentUICulture.TwoLetterISOLanguageName switch
        {
            "ko" => "ko",
            "ja" => "ja",
            _ => "en"
        };
    }

    public static void Apply(string language)
    {
        var dictionaries = Application.Current.Resources.MergedDictionaries;
        if (_english is null)
        {
            _english = Load("en");
            dictionaries.Add(_english);
        }

        if (_translation is not null)
        {
            dictionaries.Remove(_translation);
            _translation = null;
        }

        if (language != "en")
        {
            _translation = Load(language);
            dictionaries.Add(_translation);
        }

        Current = language;
    }

    public static bool Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PreferencePath)!);
            File.WriteAllText(PreferencePath, Current);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static string Text(string key) => (string)Application.Current.FindResource(key);

    public static string? TryText(string key) => Application.Current.TryFindResource(key) as string;

    private static ResourceDictionary Load(string language)
    {
        var resources = new ResourceDictionary();
        foreach (var family in new[] { "Strings", "CoreChecks", "InstallChecks", "EnvChecks", "SettingsRepair",
            "InteractiveRepair", "RepairHistory", "SessionRemoval", "Profiles", "SessionOwner" })
        {
            resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/App/Resources/{family}.{language}.xaml")
            });
        }
        return resources;
    }
}
