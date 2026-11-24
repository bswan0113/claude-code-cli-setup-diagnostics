using System.Buffers;
using System.IO;
using System.Text.Json;

namespace ClaudeLauncher;

public sealed record SavedSession(Guid Id, string Title, string Cwd, DateTime ModifiedUtc)
{
    internal string? TranscriptPath { get; init; }
    public SessionUsage? Usage { get; init; }
}

public sealed record SessionIndex(IReadOnlyList<SavedSession> Sessions, bool HasReadErrors)
{
    public IReadOnlyList<string> FallbackFolders { get; init; } = [];
    internal IReadOnlySet<Guid> SubagentIds { get; init; } = new HashSet<Guid>();
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

    // Also used for a transcript moved to the launcher's trash, whose name now includes a suffix.
    internal static SavedSession? InspectTranscript(string path, Guid id)
    {
        var chunk = ArrayPool<byte>.Shared.Rent(ChunkSize);
        var record = ArrayPool<byte>.Shared.Rent(MaxRecordSize);
        try
        {
            var failed = false;
            var item = ReadSession(path, id, chunk, record, [], ref failed);
            return !failed && item is { Cwd: { } cwd, Title: { } title }
                ? new SavedSession(id, title, cwd, item.ModifiedUtc) { TranscriptPath = path }
                : null;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk, clearArray: true);
            ArrayPool<byte>.Shared.Return(record, clearArray: true);
        }
    }

    private static SessionIndex Load()
    {
        if (WorkingFolder.Normalize(ProfileDataPaths.Config) is not { } configDirectory)
        {
            return new([], true);
        }

        var transcripts = new Dictionary<Guid, Transcript>();
        var subagentIds = new HashSet<Guid>();
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
                            var item = ReadSession(path, id, chunk, record, subagentIds, ref hasErrors);
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
                sessions.Add(new(id, title, cwd, transcript.ModifiedUtc) { TranscriptPath = transcript.FilePath });
            }
            else if (fallbackSeen.Add(cwd))
            {
                fallbackFolders.Add(cwd);
            }
        }

        // A genuine main transcript wins when several project directories contain the same ID.
        subagentIds.ExceptWith(transcripts.Keys);
        return new(sessions.ToArray(), hasErrors) { FallbackFolders = fallbackFolders.ToArray(), SubagentIds = subagentIds };
    }

    private static Transcript? ReadSession(string path, Guid id, byte[] chunk, byte[] record,
        HashSet<Guid> subagentIds, ref bool hasErrors)
    {
        var reader = new SessionRecordReader(id);
        var readFailed = false;
        var skippedLargeRecord = false;
        DateTime modifiedUtc = default;
        string? title = null;
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
                        skippedLargeRecord = true;
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

            if (reader.Cwd is { } cwd && WorkingFolder.IsClaudeScratchpad(cwd) ||
                reader.IsSubagentOnly && !readFailed && !skippedLargeRecord)
            {
                subagentIds.Add(id);
                return null;
            }
            if (!readFailed && reader.Cwd is not null)
            {
                title = ReadResumeTitle(file, id, chunk, reader.Title);
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

        readFailed |= (reader.HasUserMessage || skippedLargeRecord) && (reader.Cwd is null || title is null);
        hasErrors |= readFailed;
        return readFailed || (reader.HasUserMessage && title is not null)
            ? new Transcript(path, readFailed ? null : title, reader.Cwd, modifiedUtc)
            : null;
    }

    private static string? ReadResumeTitle(FileStream file, Guid id, byte[] buffer, string? firstPrompt)
    {
        // The CLI's JSONL display metadata comes from the transcript ends.
        var length = file.Length;
        var head = ReadTitleWindow(file, id, buffer, 0, length);
        var tail = length <= ChunkSize ? head : ReadTitleWindow(file, id, buffer, length - ChunkSize, length);
        return tail.Custom ?? head.Custom ?? tail.Automatic ?? head.Automatic ??
            tail.Summary ?? tail.LastPrompt ?? firstPrompt;
    }

    private static TitleMetadata ReadTitleWindow(FileStream file, Guid id, byte[] buffer, long offset, long length)
    {
        file.Position = Math.Max(0, offset - 1);
        var startsOnLine = offset == 0 || file.ReadByte() == '\n';
        var size = (int)Math.Min(ChunkSize, length - offset);
        var remaining = buffer.AsMemory(0, file.ReadAtLeast(buffer.AsSpan(0, size), size, false));
        var metadata = new TitleMetadata();
        if (!startsOnLine)
        {
            var newline = remaining.Span.IndexOf((byte)'\n');
            remaining = newline < 0 ? Memory<byte>.Empty : remaining[(newline + 1)..];
        }
        else if (offset == 0 && remaining.Span.StartsWith("\uFEFF"u8))
        {
            remaining = remaining[3..];
        }

        while (!remaining.IsEmpty)
        {
            var newline = remaining.Span.IndexOf((byte)'\n');
            if (newline < 0)
            {
                if (offset + size == length)
                {
                    metadata.Read(remaining, id);
                }

                break;
            }

            metadata.Read(remaining[..newline], id);
            remaining = remaining[(newline + 1)..];
        }

        return metadata;
    }

    private static string? ReadText(JsonElement row, string property)
    {
        var text = row.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim()
            : null;
        return string.IsNullOrEmpty(text) ? null : text;
    }

    private sealed class TitleMetadata
    {
        public string? Custom { get; private set; }
        public string? Automatic { get; private set; }
        public string? Summary { get; private set; }
        public string? LastPrompt { get; private set; }

        public void Read(ReadOnlyMemory<byte> line, Guid id)
        {
            if (line.Span.IndexOf("\"customTitle\""u8) < 0 && line.Span.IndexOf("\"aiTitle\""u8) < 0 &&
                line.Span.IndexOf("\"summary\""u8) < 0 && line.Span.IndexOf("\"lastPrompt\""u8) < 0)
            {
                return;
            }

            try
            {
                using var json = JsonDocument.Parse(line);
                var row = json.RootElement;
                if (row.ValueKind != JsonValueKind.Object ||
                    SessionRole.IsSubagent(row) ||
                    (row.TryGetProperty("sessionId", out var session) && (session.ValueKind != JsonValueKind.String ||
                    !session.TryGetGuid(out var parsed) || parsed != id)))
                {
                    return;
                }

                switch (ReadText(row, "type"))
                {
                    case "custom-title": Custom = ReadText(row, "customTitle") ?? Custom; break;
                    case "ai-title": Automatic = ReadText(row, "aiTitle") ?? Automatic; break;
                    case "summary": Summary = ReadText(row, "summary") ?? Summary; break;
                    case "last-prompt": LastPrompt = ReadText(row, "lastPrompt") ?? LastPrompt; break;
                }
            }
            catch (Exception error) when (error is JsonException or InvalidOperationException)
            {
                // An incomplete or malformed title entry leaves the other display candidates available.
            }
        }
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
        public bool HasUserMessage { get; private set; }
        public bool HasSession => HasUserMessage && Cwd is not null;
        private bool _hasAgentMessages;
        private bool _hasMainMessages;
        public bool IsSubagentOnly => _hasAgentMessages && !_hasMainMessages;

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
                if (hasSessionId && (id.ValueKind != JsonValueKind.String ||
                    !id.TryGetGuid(out var recordId) || sessionId != recordId))
                {
                    return true;
                }
                var conversation = IsText(row, "type", "user") || IsText(row, "type", "assistant");
                if (SessionRole.IsSubagent(row))
                {
                    _hasAgentMessages |= conversation;
                    return true;
                }
                _hasMainMessages |= conversation;

                var cwd = row.TryGetProperty("cwd", out var path) && path.ValueKind == JsonValueKind.String
                    ? WorkingFolder.Normalize(path.GetString())
                    : null;
                if (!hasSessionId)
                {
                    // A complete user row can use the UUID from its main transcript filename.
                    if (cwd is not null && TryReadUserMessage(row, out var prompt))
                    {
                        HasUserMessage = true;
                        Title ??= prompt;
                        Cwd = cwd;
                    }

                    return true;
                }

                Cwd ??= cwd;
                if (!HasUserMessage && TryReadUserMessage(row, out var title))
                {
                    HasUserMessage = true;
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

        private static bool TryReadUserMessage(JsonElement row, out string? title)
        {
            title = null;
            if (!IsText(row, "type", "user") || IsTrue(row, "isMeta") ||
                IsTrue(row, "isVisibleInTranscriptOnly") || row.TryGetProperty("toolUseResult", out _) ||
                !row.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object ||
                (message.TryGetProperty("role", out _) && !IsText(message, "role", "user")) ||
                !message.TryGetProperty("content", out var content))
            {
                return false;
            }

            if (content.ValueKind == JsonValueKind.String)
            {
                title = ReadUserText(content);
                return title is not null;
            }

            if (content.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            var parts = new List<string>();
            var hasAttachment = false;
            foreach (var block in content.EnumerateArray())
            {
                if (block.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (IsText(block, "type", "tool_result"))
                {
                    return false;
                }

                if (IsText(block, "type", "text") && block.TryGetProperty("text", out var text) &&
                    ReadUserText(text) is { } part)
                {
                    parts.Add(part);
                }

                hasAttachment |= IsText(block, "type", "image") || IsText(block, "type", "document");
            }

            title = parts.Count == 0 ? null : string.Join(' ', parts);
            return title is not null || hasAttachment;
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
