using System.Text.Json.Serialization;

namespace ClaudeLauncher;

// Use the same contracts in the launcher and the trimmed Mini. The object[] contract is the
// existing SHA-256 approval input. Its boxed values must also have registered concrete types.
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(object[]))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(Guid))]
[JsonSerializable(typeof(InstallChecks.FileStamp[]))]
[JsonSerializable(typeof(InstallChecks.ProcessIdentity))]
[JsonSerializable(typeof(InstallChecks.PathState))]
[JsonSerializable(typeof(InstallChecks.RegistryPath))]
[JsonSerializable(typeof(InstallChecks.UpdateFlags))]
[JsonSerializable(typeof(InstallChecks.PathReport))]
[JsonSerializable(typeof(InstallChecks.SignatureRequest))]
[JsonSerializable(typeof(EnvironmentChecks.ShellState))]
[JsonSerializable(typeof(PowerShellProbe.Result), TypeInfoPropertyName = "ProbeResult")]
[JsonSerializable(typeof(RepairJournal.Entry), TypeInfoPropertyName = "JournalEntry")]
[JsonSerializable(typeof(LauncherProfiles.Entry), TypeInfoPropertyName = "ProfileEntry")]
[JsonSerializable(typeof(LauncherProfiles.ProfileList))]
internal partial class DiagnosticJson : JsonSerializerContext { }
