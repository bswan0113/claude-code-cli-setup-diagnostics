using System.Buffers;
using System.Collections.Concurrent;
using System.Security;
using System.Security.Cryptography;
using System.Text.Json;

namespace ClaudeLauncher;

public enum SessionOwnerKind { None, Recorded, ArtifactOnly, Conflict, Incomplete }

public sealed record SessionUsage(long? TotalTokens, long? ContextTokens, long? ContextWindowTokens,
    double? ContextPercent, int? Compactions, bool IsPartial, bool IsWindowEstimated, string? Model)
{
    public SessionOwnerKind OwnerKind { get; init; }
    public string? OwnerHint { get; init; }
    internal string? OwnerMatchKey { get; init; }
    internal static string AccountKey(Guid id) => Convert.ToHexString(SHA256.HashData(id.ToByteArray()));
    internal static SessionUsage Unavailable { get; } = new(null, null, null, null, null, true, false, null)
        { OwnerKind = SessionOwnerKind.Incomplete };
}

/// <summary>Reads usage from the selected main transcript without retaining conversation text.</summary>
public static class SessionUsageReader
{
    private static readonly ConcurrentDictionary<string, CachedTranscript> Cache = new(StringComparer.OrdinalIgnoreCase);
    private const int ChunkSize = 64 * 1024;
    private const int MaxRowSize = 2 * 1024 * 1024;

    public static Task<SessionUsage> ReadAsync(SavedSession session, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (session.TranscriptPath is not { } path)
            return Task.FromResult(SessionUsage.Unavailable);
        return Task.Run(() => Cache.GetOrAdd(path, _ => new CachedTranscript()).Read(path, session.Id, token), token);
    }

    private sealed class CachedTranscript
    {
        private readonly object _gate = new();
        private SessionUsageAccumulator? _data;
        private SessionUsage _result = SessionUsage.Unavailable;
        private DateTime _created, _modified;
        private long _length;
        private bool _endedWithNewline;
        private byte[] _boundary = [];

        internal SessionUsage Read(string path, Guid sessionId, CancellationToken token)
        {
            lock (_gate)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    using var file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    var size = file.Length;
                    var modified = File.GetLastWriteTimeUtc(file.SafeFileHandle);
                    var created = File.GetCreationTimeUtc(file.SafeFileHandle);
                    if (_data is not null && (size, modified, created) == (_length, _modified, _created))
                        return _result;
                    var append = _data is not null && _endedWithNewline && size > _length && created == _created &&
                        Boundary(file, _length).AsSpan().SequenceEqual(_boundary);
                    if (!append)
                        _data = new SessionUsageAccumulator(sessionId);
                    file.Position = append ? _length : 0;
                    _endedWithNewline = ReadRows(file, size, _data!, token);
                    _boundary = Boundary(file, size);
                    (_length, _modified, _created) = (size, modified, created);
                    return _result = _data!.Snapshot();
                }
                catch (OperationCanceledException)
                {
                    _data = null;
                    throw;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
                {
                    _data = null;
                    return SessionUsage.Unavailable;
                }
            }
        }

        private static byte[] Boundary(FileStream file, long end)
        {
            Span<byte> buffer = stackalloc byte[(int)Math.Min(64, end)];
            file.Position = end - buffer.Length;
            file.ReadExactly(buffer);
            return SHA256.HashData(buffer);
        }
    }

    private static bool ReadRows(FileStream file, long end, SessionUsageAccumulator data, CancellationToken token)
    {
        var chunk = ArrayPool<byte>.Shared.Rent(ChunkSize);
        var row = ArrayPool<byte>.Shared.Rent(ChunkSize);
        var length = 0;
        var oversized = false;
        try
        {
            while (file.Position < end)
            {
                token.ThrowIfCancellationRequested();
                var count = file.Read(chunk.AsSpan(0, (int)Math.Min(ChunkSize, end - file.Position)));
                if (count == 0)
                    throw new EndOfStreamException();
                var bytes = chunk.AsSpan(0, count);
                while (!bytes.IsEmpty)
                {
                    var newline = bytes.IndexOf((byte)'\n');
                    var part = newline < 0 ? bytes : bytes[..newline];
                    if (!oversized)
                    {
                        int needed = length + part.Length;
                        if (needed > MaxRowSize)
                            oversized = true;
                        else
                        {
                            if (needed > row.Length)
                            {
                                var larger = ArrayPool<byte>.Shared.Rent(Math.Min(MaxRowSize, Math.Max(needed, row.Length * 2)));
                                row.AsSpan(0, length).CopyTo(larger);
                                ArrayPool<byte>.Shared.Return(row, clearArray: true);
                                row = larger;
                            }
                            part.CopyTo(row.AsSpan(length));
                            length = needed;
                        }
                    }
                    if (newline < 0)
                        break;
                    ReadRow(row.AsMemory(0, length), oversized, data);
                    length = 0;
                    oversized = false;
                    bytes = bytes[(newline + 1)..];
                }
            }
            if (length != 0 || oversized)
                ReadRow(row.AsMemory(0, length), oversized, data);
            return length == 0 && !oversized;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(row, clearArray: true);
            ArrayPool<byte>.Shared.Return(chunk, clearArray: true);
        }
    }

    private static void ReadRow(ReadOnlyMemory<byte> row, bool oversized, SessionUsageAccumulator data)
    {
        if (row.Span.StartsWith("\uFEFF"u8))
            row = row[3..];
        if (row.Span.Trim((byte)'\r').IsEmpty)
            return;
        try
        {
            // Discard tool output and progress wrappers before allocating a JSON document. Restrict
            // inspection to the root type so a pasted transcript cannot contribute usage.
            var reader = new Utf8JsonReader(row.Span, isFinalBlock: !oversized, state: default);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                data.MarkGap();
                return;
            }
            bool accountRecord = false;
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var type = reader.ValueTextEquals("type"u8);
                if (!reader.Read())
                    break;
                if (type && reader.TokenType == JsonTokenType.String)
                {
                    if (reader.ValueTextEquals("user"u8) || reader.ValueTextEquals("progress"u8))
                        return;
                    accountRecord = reader.ValueTextEquals("session-owner"u8) ||
                        reader.ValueTextEquals("artifact-autoreact-ledger"u8);
                    break;
                }
                if (!reader.TrySkip())
                    break;
            }
            if (oversized)
            {
                data.MarkGap();
                return;
            }
            if (!accountRecord && row.Span.IndexOf("\"assistant\""u8) < 0 && row.Span.IndexOf("\"compact_boundary\""u8) < 0 &&
                row.Span.IndexOf("\"context_window\""u8) < 0)
                return;
            using var document = JsonDocument.Parse(row);
            data.Observe(document.RootElement);
        }
        catch (JsonException)
        {
            data.MarkGap();
        }
    }
}
