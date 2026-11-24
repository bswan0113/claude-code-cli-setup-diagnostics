using System.Security.Cryptography;
using System.Text;

namespace ClaudeLauncher;

internal static class ProfileDataPaths
{
    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private static string StandardRoot => Path.Combine(Home, ".claude");
    private static string? Override => Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
    internal static string Config => LauncherProfiles.IsAdditional ? LauncherProfiles.Current.ConfigRoot :
        string.IsNullOrWhiteSpace(Override) ? StandardRoot : Override;
    internal static string AccountState => Path.Combine(!LauncherProfiles.IsAdditional && string.IsNullOrWhiteSpace(Override)
        ? Home : Config, ".claude.json");

    // Preserve existing launcher data for the ordinary Claude profile.
    internal static string LocalStore(string legacyPath)
    {
        string root = Path.TrimEndingDirectorySeparator(Config);
        if (string.Equals(root, StandardRoot, StringComparison.OrdinalIgnoreCase))
            return legacyPath;
        string id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root.ToUpperInvariant())));
        return Path.Combine(Path.GetDirectoryName(legacyPath)!, "profiles", id, Path.GetFileName(legacyPath));
    }
}
