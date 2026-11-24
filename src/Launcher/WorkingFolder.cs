using System.IO;

namespace ClaudeLauncher;

internal static class WorkingFolder
{
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
