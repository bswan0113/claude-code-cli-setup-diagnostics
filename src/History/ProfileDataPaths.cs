using System.Security.Cryptography;
using System.Text;

namespace ClaudeLauncher;

internal static class ProfileDataPaths
{
    internal static readonly string LocalData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private static string StandardRoot => Path.Combine(Home, ".claude");
    internal static string Config => LauncherProfiles.Current.ConfigRoot;
    internal static string AccountState
    {
        get
        {
            var profile = LauncherProfiles.Current;
            return Path.Combine(profile.UsesStandardAccountState ? Home : profile.ConfigRoot, ".claude.json");
        }
    }

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
