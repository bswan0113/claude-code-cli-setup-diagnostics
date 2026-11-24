using System.ComponentModel;
using System.Security;
using System.Text.Json;

namespace ClaudeLauncher;

internal static partial class EnvironmentChecks
{
    private const string InstallGitAction = "InstallGitWinGet";
    private const string GitInstallTarget = "Git for Windows · Git.Git · WinGet (--source winget --scope user)";

    private static async Task<CheckItem> PrepareGitAsync(string cwd, ShellState? shell, CancellationToken token)
    {
        var row = GitItem(shell);
        if (row.Key != "GitNotFound")
            return row;
        var manager = await InstallChecks.GetGitPackageManagerAsync(token).ConfigureAwait(false);
        if (manager is null)
            return row;
        return GitApproval(cwd, row, manager);
    }

    private static CheckItem GitApproval(string cwd, CheckItem row, string manager) => row with
    {
        Action = InstallGitAction,
        Target = GitInstallTarget,
        Revision = Revision(cwd, InstallGitAction, GitInstallTarget, manager)
    };

    private static async Task<bool> ApplyGitInstallAsync(string cwd, CheckItem approved, CancellationToken token)
    {
        var shell = await ReadShellAsync(cwd, token).ConfigureAwait(false);
        var current = GitItem(shell);
        if (current.Key != "GitNotFound")
            return false;
        var manager = await InstallChecks.GetGitPackageManagerAsync(token).ConfigureAwait(false);
        if (manager is null || GitApproval(cwd, current, manager) != approved || Diagnostics.IsWinGetRunningInCurrentSession())
            return false;

        if (!await InstallChecks.InstallGitPackageAsync(manager, token).ConfigureAwait(false))
            return false;
        var checkedShell = await ReadShellAsync(cwd, token).ConfigureAwait(false);
        return GitItem(checkedShell).Key == "GitFound";
    }
}

internal static partial class InstallChecks
{
    // Use the current user's registered, signed App Installer package. A same-name
    // executable on PATH, in the working folder or in a writable alias directory is insufficient.
    private const string FindGitPackageManager = """
        $ErrorActionPreference = 'Stop'
        $module = [IO.Path]::Combine($PSHOME, 'Modules', 'Appx', 'Appx.psd1')
        Microsoft.PowerShell.Core\Import-Module -Name $module
        foreach ($p in @(Appx\Get-AppxPackage -Name Microsoft.DesktopAppInstaller)) {
            if ($p.PackageFamilyName -cne 'Microsoft.DesktopAppInstaller_8wekyb3d8bbwe' -or
                $p.IsDevelopmentMode -or $p.IsFramework -or $p.IsResourcePackage -or
                $p.SignatureKind -notin @('System', 'Store') -or $p.Status -ne 'Ok') { continue }
            foreach ($name in @('winget.exe', 'AppInstallerCLI.exe')) {
                $path = [IO.Path]::Combine($p.InstallLocation, $name)
                if ([IO.File]::Exists($path)) { [Console]::WriteLine($path); exit 0 }
            }
        }
        exit 1
        """;

    internal static async Task<string?> GetGitPackageManagerAsync(CancellationToken token)
    {
        try
        {
            var lookup = await RunPowerShellAsync(FindGitPackageManager, token, _ => { }).ConfigureAwait(false);
            var path = lookup?.Output.Trim();
            if (lookup?.ExitCode != 0 || string.IsNullOrEmpty(path) || !Path.IsPathFullyQualified(path) ||
                path.Any(char.IsControl) || !File.Exists(path))
                return null;
            var source = await RunToolAsync(path, ["source", "export", "winget", "--disable-interactivity"],
                token, TimeSpan.FromSeconds(12)).ConfigureAwait(false);
            if (source?.ExitCode != 0)
                return null;
            using var json = JsonDocument.Parse(source.Output);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("Name", out var name) || name.ValueKind != JsonValueKind.String || name.GetString() != "winget" ||
                !root.TryGetProperty("Arg", out var url) || url.ValueKind != JsonValueKind.String ||
                !Uri.TryCreate(url.GetString(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
                !uri.IsDefaultPort || uri.UserInfo.Length != 0 ||
                !uri.IdnHost.Equals("cdn.winget.microsoft.com", StringComparison.OrdinalIgnoreCase))
                return null;
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or SecurityException or JsonException)
        {
            return null;
        }
    }

    internal static async Task<bool> InstallGitPackageAsync(string manager, CancellationToken token)
    {
        var installed = await RunToolAsync(manager,
            ["install", "--id", "Git.Git", "--exact", "--source", "winget", "--scope", "user",
             "--no-upgrade", "--silent", "--accept-source-agreements", "--accept-package-agreements", "--disable-interactivity"],
            token, TimeSpan.FromMinutes(10)).ConfigureAwait(false);
        if (installed?.ExitCode != 0)
            return false;
        var listed = await RunToolAsync(manager,
            ["list", "--id", "Git.Git", "--exact", "--source", "winget", "--scope", "user", "--disable-interactivity"],
            token, TimeSpan.FromSeconds(45)).ConfigureAwait(false);
        return listed?.ExitCode == 0;
    }
}
