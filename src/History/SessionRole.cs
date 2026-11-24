using System.Text.Json;

namespace ClaudeLauncher;

internal static class SessionRole
{
    // Only inspect the envelope. A main conversation can mention agents or contain their tool results.
    internal static bool IsSubagent(JsonElement row)
    {
        if (row.ValueKind != JsonValueKind.Object)
            return false;
        return Flag(row, "isSidechain") || Flag(row, "isSubagent") ||
            Nonempty(row, "agentId") || Nonempty(row, "parent_tool_use_id") || Nonempty(row, "parentToolUseId") ||
            IsAgentKind(row, "kind") || IsAgentKind(row, "entrypoint");
    }

    private static bool Flag(JsonElement row, string name) =>
        row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
    private static bool Nonempty(JsonElement row, string name) =>
        row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(value.GetString());
    private static bool IsAgentKind(JsonElement row, string name) =>
        row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
        (value.ValueEquals("subagent") || value.ValueEquals("sub-agent"));
}
