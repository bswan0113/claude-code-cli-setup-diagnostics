namespace ClaudeLauncher;

internal static class SignedExecutable
{
    // Resolve a WinGet link once. Callers lock, check and start this literal target.
    internal const string ResolvePath = """
        function Resolve-LauncherExecutable([string]$path) {
            $entry = Microsoft.PowerShell.Management\Get-Item -LiteralPath $path -Force -ErrorAction Stop
            for ($depth = 0; ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint); $depth++) {
                $targets = @($entry.Target)
                if ($depth -ge 8 -or $targets.Length -ne 1 -or !$targets[0]) { throw 'Unresolved executable' }
                $target = [string]$targets[0]
                if (![IO.Path]::IsPathRooted($target)) { $target = [IO.Path]::Combine($entry.DirectoryName, $target) }
                $entry = Microsoft.PowerShell.Management\Get-Item -LiteralPath ([IO.Path]::GetFullPath($target)) -Force -ErrorAction Stop
            }
            for ($parent = $entry.Directory; $null -ne $parent; $parent = $parent.Parent) {
                if ($parent.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Redirected executable directory' }
            }
            return $entry.FullName
        }
        """;
}
