using System.IO;

namespace ClaudeLauncher;

internal static class WorkingFolder
{
    private static readonly string[] ClaudeTempRoots = new[]
    {
        Path.Combine(Path.GetTempPath(), "claude"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp", "claude")
    }.Select(root => Path.GetFullPath(root) + Path.DirectorySeparatorChar)
        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    // Claude's per-session scratch workspace: <temp>/claude/<project hash>/<session UUID>/scratchpad[/...].
    internal static bool IsClaudeScratchpad(string folder)
    {
        foreach (var root in ClaudeTempRoots)
        {
            if (!folder.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                continue;
            var parts = folder[root.Length..].Split(Path.DirectorySeparatorChar, 4);
            if (parts.Length >= 3 && parts[0].Length == 16 && parts[0].All(char.IsAsciiHexDigit) &&
                Guid.TryParseExact(parts[1], "D", out _) && parts[2].Equals("scratchpad", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    internal static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            return null;
        }

        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception error) when (error is ArgumentException or IOException or NotSupportedException)
        {
            return null;
        }
    }
}
