using System.IO;
using System.Text.Json;
using ClaudeLauncher;

internal static partial class Program
{
    private static string TestSessionMetadata(string root)
    {
        var config = MakeDirectory(root, "session-metadata-config");
        var project = MakeDirectory(config, Path.Combine("projects", "fixture-project"));
        var cwdA = MakeDirectory(root, "세션 A [편집] & 폴더");
        var cwdB = MakeDirectory(root, "세션 B");
        var cwdLegacy = MakeDirectory(root, "legacy-only");
        var textId = Guid.NewGuid();
        var blocksId = Guid.NewGuid();
        var text = WriteSession(project, textId, Older,
            JsonSerializer.Serialize(new { type = "assistant", cwd = cwdA, message = new { role = "assistant", content = "qa-assistant-marker" } }),
            UserLine(textId, cwdA, "qa-meta-marker", Latest, isMeta: true),
            UserLine(textId, cwdA, new object[] { new { type = "tool_result", content = "qa-tool-marker" } }, Latest),
            UserLine(textId, cwdA, "첫 사용자 메시지", Latest),
            UserLine(textId, cwdA, "qa-second-user-marker", Latest.AddHours(1)));
        var blocks = WriteSession(project, blocksId, Newer,
            UserLine(blocksId, cwdB, new object[]
            {
                new { type = "text", text = "첫 번째 조각" },
                new { type = "text", text = "두 번째 조각" },
            }, Older.AddDays(-1)));

        var sidechainId = Guid.NewGuid();
        var emptyId = Guid.NewGuid();
        var subagentId = Guid.NewGuid();
        WriteSession(project, sidechainId, Latest, UserLine(sidechainId, cwdA, "qa-sidechain-marker", Newer, isSidechain: true));
        WriteSession(project, emptyId, Latest, UserLine(emptyId, cwdA, "", Latest));
        WriteSession(MakeDirectory(project, Path.Combine(textId.ToString("D"), "subagents")),
            subagentId, Latest, UserLine(subagentId, cwdA, "qa-subagent-marker", Latest));
        File.WriteAllText(Path.Combine(config, "history.jsonl"),
            JsonSerializer.Serialize(new { project = cwdLegacy, display = "qa-legacy-marker", timestamp = Latest.ToString("O") }));

        var before = Fingerprint(config);
        var index = LoadSessionFixture(config);
        var sessions = index.Sessions;
        Require(!index.HasReadErrors, "Valid, empty, or excluded files were reported as unreadable.");
        Require(sessions.Count == 2, "Expected two main sessions. Empty, sidechain, subagent and legacy records are excluded.");
        Require(sessions[0] == new SavedSession(blocksId, sessions[0].Title, cwdB, File.GetLastWriteTimeUtc(blocks)) &&
            sessions[0].Title.StartsWith("첫 번째 조각", StringComparison.Ordinal) &&
            sessions[0].Title.Contains("두 번째 조각", StringComparison.Ordinal),
            "Text blocks, the JSONL cwd, or file-mtime ordering was incorrect.");
        Require(sessions[1] == new SavedSession(textId, "첫 사용자 메시지", cwdA, File.GetLastWriteTimeUtc(text)),
            "Metadata, tool results or later messages displaced the first actual user title, cwd or file mtime.");
        Require(before.SequenceEqual(Fingerprint(config)), "Session indexing modified fixture data.");
        return "Two main sessions sorted by file UTC mtime. Exact GUID/cwd, string and text blocks verified; legacy and excluded records absent.";
    }

    private static string TestSessionDamage(string root)
    {
        var config = MakeDirectory(root, "session-damage-config");
        var project = MakeDirectory(config, Path.Combine("projects", "damaged-project"));
        var cwd = MakeDirectory(root, "손상 옆 정상 세션");
        var fallback = MakeDirectory(root, "손상 세션 [재선택 없이] & 테스트");
        var goodId = Guid.NewGuid();
        WriteSession(project, goodId, Older, UserLine(goodId, cwd, "정상 세션", Older));
        WriteSession(project, Guid.NewGuid(), Newer, "{\"type\":\"user\",\"message\":");
        var damagedId = Guid.NewGuid();
        WriteSession(project, damagedId, Latest, "{broken-before-metadata",
            UserLine(damagedId, fallback, "qa-damaged-entry-must-not-resume", Latest));
        var secondDamagedId = Guid.NewGuid();
        WriteSession(project, secondDamagedId, Latest,
            JsonSerializer.Serialize(new { type = "assistant", sessionId = secondDamagedId, cwd = fallback + Path.DirectorySeparatorChar }),
            "{broken-after-cwd");
        var blankId = Guid.NewGuid();
        WriteSession(project, blankId, Latest, UserLine(blankId, cwd, "", Latest));

        var before = Fingerprint(config);
        var index = LoadSessionFixture(config);
        Require(index.HasReadErrors, "A malformed main JSONL did not signal the fallback condition.");
        Require(index.Sessions.Count == 1 && index.Sessions[0].Id == goodId && index.Sessions[0].Cwd == cwd,
            "Malformed main JSONL hid a readable session or polluted the normal list.");
        Require(index.FallbackFolders.SequenceEqual([fallback], StringComparer.OrdinalIgnoreCase),
            "Recovered cwd must appear once as a normalized fallback. Unknown cwd and empty sessions must not create folder rows.");
        Require(before.SequenceEqual(Fingerprint(config)), "Damaged source files were modified while indexing.");
        return "Known damaged cwd offered once; unknown cwd signaled partial read; the healthy session remained resumable.";
    }

    private static SessionIndex LoadSessionFixture(string config)
    {
        var originalConfig = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        try
        {
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", config, EnvironmentVariableTarget.Process);
            return SessionHistory.LoadAsync().GetAwaiter().GetResult();
        }
        finally
        {
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", originalConfig, EnvironmentVariableTarget.Process);
        }
    }

    private static string WriteSession(string project, Guid id, DateTime fileTime, params string[] lines)
    {
        var file = Path.Combine(project, $"{id:D}.jsonl");
        File.WriteAllLines(file, lines);
        File.SetLastWriteTimeUtc(file, fileTime);
        return file;
    }

    private static string UserLine(Guid sessionId, string cwd, object content, DateTime time,
        bool isMeta = false, bool isSidechain = false) => JsonSerializer.Serialize(new
        {
            type = "user", sessionId, isMeta, isSidechain, cwd,
            message = new { role = "user", content }, timestamp = time,
        });
}
