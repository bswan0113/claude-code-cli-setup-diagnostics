using System.Globalization;
using System.Text;

namespace ClaudeLauncher;

internal static class UiLanguage
{
    private static readonly string PreferencePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ClaudeCodeSetupDiagnostics", "language.txt");

    private sealed record Catalog(string Language, Dictionary<string, string> Strings);
    private static readonly Catalog English = Load("en");
    private static Catalog _current = English;
    public static string Current => Volatile.Read(ref _current).Language;

    public static string GetInitialLanguage()
    {
        try
        {
            var saved = File.ReadAllText(PreferencePath).Trim();
            if (Supported(saved))
                return saved;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }

        var display = CultureInfo.CurrentUICulture;
        if (display.Name is "zh" or "zh-CN" or "zh-SG" ||
            display.Name.StartsWith("zh-Hans", StringComparison.OrdinalIgnoreCase))
            return "zh-Hans";
        return display.TwoLetterISOLanguageName switch { "ko" => "ko", "ja" => "ja", _ => "en" };
    }

    public static void Apply(string language)
    {
        if (!Supported(language))
            language = "en";
        if (Current != language)
            Volatile.Write(ref _current, language == "en" ? English : Load(language));
    }

    public static bool Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PreferencePath)!);
            File.WriteAllText(PreferencePath, Current);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static string Text(string key) => TryText(key) ?? key;

    public static string? TryText(string key)
    {
        var strings = Volatile.Read(ref _current).Strings;
        return strings.TryGetValue(key, out var value) ? value : English.Strings.GetValueOrDefault(key);
    }

    private static bool Supported(string language) => language is "en" or "ko" or "zh-Hans" or "ja";

    private static Catalog Load(string language)
    {
        using var stream = typeof(UiLanguage).Assembly.GetManifestResourceStream($"MiniStrings.{language}.bin")
            ?? throw new InvalidDataException($"Missing Mini language resource for {language}.");
        using var reader = new BinaryReader(stream, Encoding.UTF8);
        int count = reader.ReadInt32();
        var strings = new Dictionary<string, string>(count, StringComparer.Ordinal);
        for (int i = 0; i < count; i++)
            strings.Add(reader.ReadString(), reader.ReadString());
        return new Catalog(language, strings);
    }
}
