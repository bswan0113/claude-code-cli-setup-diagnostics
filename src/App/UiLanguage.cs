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
        if (dictionaries.Count == 0)
            dictionaries.Add(Load("en"));

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

    private static ResourceDictionary Load(string language) => new()
    {
        Source = new Uri($"pack://application:,,,/App/Resources/Strings.{language}.xaml")
    };
}
