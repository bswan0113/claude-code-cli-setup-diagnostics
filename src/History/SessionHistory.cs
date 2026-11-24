using System.Buffers;
using System.IO;
using System.Text.Json;

namespace ClaudeLauncher;

public sealed record SavedSession(Guid Id, string Title, string Cwd, DateTime ModifiedUtc);

public sealed record SessionIndex(IReadOnlyList<SavedSession> Sessions, bool HasReadErrors)
{
    public IReadOnlyList<string> FallbackFolders { get; init; } = [];
}

public static class SessionHistory
{
    private const int ChunkSize = 64 * 1024;
    private const int MaxRecordSize = 1024 * 1024;

    private static readonly EnumerationOptions Enumeration = new()
    {
        IgnoreInaccessible = false,
        AttributesToSkip = FileAttributes.ReparsePoint,
    };

    public static Task<SessionIndex> LoadAsync() => Task.Run(Load);

    private static SessionIndex Load()
    {
        var configured = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        var root = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude")
            : configured;
        if (WorkingFolder.Normalize(root) is not { } configDirectory)
        {
            return new([], true);
        }

        var transcripts = new Dictionary<Guid, Transcript>();
        var hasErrors = false;
        var chunk = ArrayPool<byte>.Shared.Rent(ChunkSize);
        var record = ArrayPool<byte>.Shared.Rent(MaxRecordSize);
        try
        {
            foreach (var project in Directory.EnumerateDirectories(Path.Combine(configDirectory, "projects"), "*", Enumeration))
            {
                try
                {
                    foreach (var path in Directory.EnumerateFiles(project, "*.jsonl", Enumeration))
                    {
                        if (Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "D", out var id) && id != Guid.Empty)
                        {
                            var item = ReadSession(path, id, chunk, record, ref hasErrors);
                            if (item is not null && (!transcripts.TryGetValue(id, out var previous) ||
                                item.IsPreferredTo(previous)))
                            {
                                transcripts[id] = item;
                            }
                        }
                    }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    hasErrors = true;
                }
            }
        }
        catch (DirectoryNotFoundException)
        {
            // Claude has not saved projects in this profile yet.
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            hasErrors = true;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk, clearArray: true);
            ArrayPool<byte>.Shared.Return(record, clearArray: true);
        }

        var sessions = new List<SavedSession>();
        var fallbackFolders = new List<string>();
        var available = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var fallbackSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, transcript) in transcripts.OrderByDescending(item => item.Value.ModifiedUtc)
            .ThenBy(item => item.Key))
        {
            if (transcript.Cwd is not { } cwd)
            {
                continue;
            }

            if (!available.TryGetValue(cwd, out var exists))
            {
                available[cwd] = exists = Directory.Exists(cwd);
            }

            if (!exists)
            {
                continue;
            }

            if (transcript.Title is { } title)
            {
                sessions.Add(new(id, title, cwd, transcript.ModifiedUtc));
            }
            else if (fallbackSeen.Add(cwd))
            {
                fallbackFolders.Add(cwd);
            }
        }

        return new(sessions.ToArray(), hasErrors) { FallbackFolders = fallbackFolders.ToArray() };
    }

    private static Transcript? ReadSession(string path, Guid id, byte[] chunk, byte[] record,
        ref bool hasErrors)
    {
        var reader = new SessionRecordReader(id);
        var readFailed = false;
        DateTime modifiedUtc = default;
        try
        {
            using var file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            modifiedUtc = File.GetLastWriteTimeUtc(file.SafeFileHandle);
            var length = 0;
            var oversized = false;
            int count;
            while (!reader.HasSession && (count = file.Read(chunk, 0, ChunkSize)) > 0)
            {
                var remaining = chunk.AsSpan(0, count);
                while (!remaining.IsEmpty && !reader.HasSession)
                {
                    var newline = remaining.IndexOf((byte)'\n');
                    var slice = newline < 0 ? remaining : remaining[..newline];
                    if (!oversized && slice.Length <= MaxRecordSize - length)
                    {
                        slice.CopyTo(record.AsSpan(length));
                        length += slice.Length;
                    }
                    else
                    {
                        oversized = true;
                        readFailed = true;
                    }

                    if (newline < 0)
                    {
                        break;
                    }

                    if (!oversized)
                    {
                        readFailed |= !reader.Read(record.AsMemory(0, length));
                    }

                    length = 0;
                    oversized = false;
                    remaining = remaining[(newline + 1)..];
                }
            }

            if (!oversized && length > 0 && !reader.HasSession)
            {
                readFailed |= !reader.Read(record.AsMemory(0, length));
            }
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            readFailed = true;
        }

        readFailed |= reader.Title is not null && reader.Cwd is null;
        hasErrors |= readFailed;
        return readFailed || reader.HasSession
            ? new Transcript(path, readFailed ? null : reader.Title, reader.Cwd, modifiedUtc)
            : null;
    }

    private sealed record Transcript(string FilePath, string? Title, string? Cwd, DateTime ModifiedUtc)
    {
        public bool IsPreferredTo(Transcript other)
        {
            if ((Title is null) != (other.Title is null))
            {
                return Title is not null;
            }

            return ModifiedUtc > other.ModifiedUtc || (ModifiedUtc == other.ModifiedUtc &&
                StringComparer.Ordinal.Compare(FilePath, other.FilePath) < 0);
        }
    }

    private sealed class SessionRecordReader(Guid sessionId)
    {
        public string? Title { get; private set; }
        public string? Cwd { get; private set; }
        public bool HasSession => Title is not null && Cwd is not null;

        public bool Read(ReadOnlyMemory<byte> line)
        {
            if (line.Span.StartsWith("\uFEFF"u8))
            {
                line = line[3..];
            }

            if (line.IsEmpty || line.Span.SequenceEqual("\r"u8))
            {
                return true;
            }

            try
            {
                using var document = JsonDocument.Parse(line);
                var row = document.RootElement;
                if (row.ValueKind != JsonValueKind.Object)
                {
                    return false;
                }

                var hasSessionId = row.TryGetProperty("sessionId", out var id);
                if (IsTrue(row, "isSidechain") || (hasSessionId && (id.ValueKind != JsonValueKind.String ||
                    !id.TryGetGuid(out var recordId) || sessionId != recordId)))
                {
                    return true;
                }

                var cwd = row.TryGetProperty("cwd", out var path) && path.ValueKind == JsonValueKind.String
                    ? WorkingFolder.Normalize(path.GetString())
                    : null;
                if (!hasSessionId)
                {
                    // A complete user row can use the UUID from its main transcript filename.
                    if (cwd is not null && TryReadPrompt(row) is { } prompt)
                    {
                        Title ??= prompt;
                        Cwd = cwd;
                    }

                    return true;
                }

                Cwd ??= cwd;
                if (Title is null && TryReadPrompt(row) is { } title)
                {
                    Title = title;
                    Cwd = cwd ?? Cwd;
                }

                return true;
            }
            catch (Exception error) when (error is JsonException or InvalidOperationException)
            {
                return false;
            }
        }

        private static string? TryReadPrompt(JsonElement row)
        {
            if (!IsText(row, "type", "user") || IsTrue(row, "isMeta") ||
                IsTrue(row, "isVisibleInTranscriptOnly") || row.TryGetProperty("toolUseResult", out _) ||
                !row.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object ||
                (message.TryGetProperty("role", out _) && !IsText(message, "role", "user")) ||
                !message.TryGetProperty("content", out var content))
            {
                return null;
            }

            if (content.ValueKind == JsonValueKind.String)
            {
                return ReadUserText(content);
            }

            if (content.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var parts = new List<string>();
            foreach (var block in content.EnumerateArray())
            {
                if (block.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (IsText(block, "type", "tool_result"))
                {
                    return null;
                }

                if (IsText(block, "type", "text") && block.TryGetProperty("text", out var text) &&
                    ReadUserText(text) is { } part)
                {
                    parts.Add(part);
                }
            }

            return parts.Count == 0 ? null : string.Join(' ', parts);
        }

        private static string? ReadUserText(JsonElement item)
        {
            var value = item.ValueKind == JsonValueKind.String ? item.GetString()?.Trim() : null;
            if (string.IsNullOrEmpty(value) || value.StartsWith("<local-command-caveat>", StringComparison.Ordinal) ||
                value.StartsWith("<local-command-stdout>", StringComparison.Ordinal) ||
                value.StartsWith("<local-command-stderr>", StringComparison.Ordinal) ||
                value.StartsWith("<system-reminder>", StringComparison.Ordinal))
            {
                return null;
            }

            return value;
        }

        private static bool IsTrue(JsonElement item, string key) =>
            item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.True;

        private static bool IsText(JsonElement item, string key, string expected) =>
            item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String &&
            value.ValueEquals(expected);
    }
}
