using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace ClaudeLauncher;

internal static partial class RepairJournal
{
    internal sealed record PathEntry(int At, string Text);
    internal sealed record PathChange(string Directory, string BeforeHash, string AfterHash, PathEntry[] Previous,
        bool HadValue, RegistryValueKind Kind);

    // Only save the moved directory's entries and hashes. Other PATH entries and arbitrary environment values
    // must never be serialized into a repair history file.
    internal static void PreparePaths(string directory, string? newUser, RegistryValueKind kind, string newProcess)
    {
        // A refresh performed as a side effect of an install does not undo the package installation.
        if (Current.Value?.Entry.Action is not ("RefreshClaudeProcessPath" or "PreferClaudePath"))
            return;
        using var environment = Registry.CurrentUser.OpenSubKey("Environment");
        var raw = environment?.GetValue("Path", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
        var process = Environment.GetEnvironmentVariable("Path");
        var userChange = newUser is null || newUser == raw ? null : DescribePath(directory, raw, newUser, kind);
        var processChange = newProcess == process ? null : DescribePath(directory, process, newProcess, RegistryValueKind.String);
        if (userChange is not null || processChange is not null)
            Prepare(new UndoPlan("Path", userChange, processChange, Instance));
    }

    private static PathChange DescribePath(string entry, string? old, string next, RegistryValueKind kind)
    {
        var removed = Parts(old).Select((text, i) => new PathEntry(i, text)).Where(row => SameEntry(row.Text, entry, kind)).ToArray();
        var change = new PathChange(entry, Hash(old), Hash(next), removed, old is not null, kind);
        if (!TryOriginal(next, change, out _))
            throw new IOException("The proposed PATH mutation could not be recorded exactly.");
        return change;
    }

    private static string[] Parts(string? raw) => string.IsNullOrEmpty(raw) ? [] : raw.Split(';');
    private static string Hash(string? value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value is null ? "0" : "1" + value)));

    private static bool SameEntry(string raw, string directory, RegistryValueKind kind)
    {
        var value = kind == RegistryValueKind.ExpandString ? Environment.ExpandEnvironmentVariables(raw) : raw;
        var normalized = WorkingFolder.Normalize(value.Trim().Trim('"'));
        return normalized is not null && string.Equals(normalized, directory, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryOriginal(string? current, PathChange change, out string? original)
    {
        original = null;
        if (Hash(current) != change.AfterHash)
            return false;
        var entries = Parts(current).Where(entry => !SameEntry(entry, change.Directory, change.Kind)).ToList();
        foreach (var row in change.Previous)
        {
            if (row.At < 0 || row.At > entries.Count || !SameEntry(row.Text, change.Directory, change.Kind))
                return false;
            entries.Insert(row.At, row.Text);
        }
        original = change.HadValue ? string.Join(';', entries) : null;
        // Appending to a PATH ending in a separator consumes that separator.
        if (change.HadValue && Hash(original) != change.BeforeHash && Hash(original + ";") == change.BeforeHash)
            original += ";";
        return Hash(original) == change.BeforeHash;
    }

    private static bool RestorePaths(UndoPlan plan)
    {
        using var key = Registry.CurrentUser.OpenSubKey("Environment", writable: plan.UserPath is not null);
        var currentUser = key?.GetValue("Path", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
        string? userBefore = null;
        bool changeUser = false;
        if (plan.UserPath is { } user)
        {
            if (key is null || currentUser is not null && key.GetValueKind("Path") != user.Kind)
                return false;
            changeUser = Hash(currentUser) != user.BeforeHash;
            if (changeUser && !TryOriginal(currentUser, user, out userBefore))
                return false;
        }

        var currentProcess = Environment.GetEnvironmentVariable("Path");
        string? processBefore = null;
        var changeProcess = false;
        if (plan.ProcessPath is { } process && plan.Instance == Instance)
        {
            changeProcess = Hash(currentProcess) != process.BeforeHash;
            if (changeProcess && !TryOriginal(currentProcess, process, out processBefore))
                return false;
        }
        // A restarted launcher may have inherited the recorded user PATH change. Revert only if it
        // still contains exactly the path we approved, preserving any independently changed process PATH.
        else if (plan.UserPath is not null && plan.ProcessPath is { } inherited &&
            TryOriginal(currentProcess, inherited, out processBefore))
            changeProcess = true;

        if (changeUser)
        {
            if (!Equals(key!.GetValue("Path", null, RegistryValueOptions.DoNotExpandEnvironmentNames), currentUser))
                return false;
            if (userBefore is null)
                key!.DeleteValue("Path", throwOnMissingValue: false);
            else
                key!.SetValue("Path", userBefore, plan.UserPath!.Kind);
            if (!Equals(key!.GetValue("Path", null, RegistryValueOptions.DoNotExpandEnvironmentNames), userBefore))
                return false;
            NotifyEnvironment();
        }
        if (changeProcess)
            Environment.SetEnvironmentVariable("Path", processBefore, EnvironmentVariableTarget.Process);
        return !changeProcess || Environment.GetEnvironmentVariable("Path") == processBefore;
    }

    internal static void PrepareUpdater(string? beforeProcess, string? next, string? nextProcess)
    {
        const string name = "DISABLE_AUTOUPDATER";
        using var key = Registry.CurrentUser.OpenSubKey("Environment");
        var raw = key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (raw is not null && raw is not string)
            throw new IOException("Unsupported update flag type.");
        if (!FlagValue((string?)raw) || !FlagValue(beforeProcess) || !FlagValue(next) || !FlagValue(nextProcess))
            throw new IOException("Unsupported update flag.");
        Prepare(new UndoPlan("Updater", Instance: Instance, Previous: (string?)raw, Expected: next,
            PreviousProcess: beforeProcess, ExpectedProcess: nextProcess, ValueKind: raw is null ? null : (int)key!.GetValueKind(name)));
    }

    internal static bool CanTrackUpdater(string? process)
    {
        using var key = Registry.CurrentUser.OpenSubKey("Environment");
        const string name = "DISABLE_AUTOUPDATER";
        var raw = key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        return FlagValue(process) && (raw is null || raw is string value && FlagValue(value) &&
            key!.GetValueKind(name) is RegistryValueKind.String or RegistryValueKind.ExpandString);
    }

    private static bool FlagValue(string? value) => value is null || value.ToLowerInvariant() is "" or "0" or "1" or "true" or "false" or "yes" or "no" or "on" or "off";

    private static bool RestoreUpdater(UndoPlan plan)
    {
        const string name = "DISABLE_AUTOUPDATER";
        using var key = Registry.CurrentUser.CreateSubKey("Environment");
        var value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        var process = Environment.GetEnvironmentVariable(name);
        var originalKind = (RegistryValueKind)(plan.ValueKind ?? (int)RegistryValueKind.String);
        if (!InstallChecks.UserUpdaterMutable() || !FlagValue(plan.Previous) || !FlagValue(plan.Expected) ||
            originalKind is not (RegistryValueKind.String or RegistryValueKind.ExpandString) ||
            !Equals(value, plan.Expected) && !Equals(value, plan.Previous) ||
            plan.Instance == Instance && process != plan.ExpectedProcess && process != plan.PreviousProcess ||
            plan.Instance != Instance && process is not null && process != plan.Expected && process != plan.Previous)
            return false;
        if (value is not null && Equals(value, plan.Expected) && key.GetValueKind(name) != RegistryValueKind.String)
            return false;
        if (plan.Previous is null)
            key.DeleteValue(name, throwOnMissingValue: false);
        else
            key.SetValue(name, plan.Previous, originalKind);
        if (plan.Instance == Instance || process == plan.Expected)
            Environment.SetEnvironmentVariable(name, plan.Instance == Instance ? plan.PreviousProcess : plan.Previous);
        NotifyEnvironment();
        return Equals(key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames), plan.Previous);
    }

    private static void NotifyEnvironment() => SendMessageTimeout(new IntPtr(0xffff), 0x001a, 0, "Environment", 2, 500, out _);

    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, nuint parameter, string area,
        uint flags, uint timeout, out nuint result);
}
