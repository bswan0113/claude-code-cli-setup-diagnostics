using System.Text;
using System.Text.Json;

namespace ClaudeLauncher;

internal static partial class SettingsDiagnostics
{
    private static bool White(byte value) => value is (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r';
    private static int SkipSpace(ReadOnlySpan<byte> bytes, int index)
    {
        while (index < bytes.Length && White(bytes[index])) index++;
        return index;
    }

    private static (Scope, byte[], string?, string?)? Change(string path, byte[] original, CancellationToken token)
    {
        // A single extra comma immediately before a closing delimiter has one lossless fix.
        var inside = false;
        var escaped = false;
        var trailing = -1;
        for (var i = 0; i < original.Length; i++)
        {
            byte b = original[i];
            if (inside)
            {
                if (escaped) escaped = false;
                else if (b == '\\') escaped = true;
                else if (b == '"') inside = false;
            }
            else if (b == '"') inside = true;
            else if (b == ',')
            {
                var next = SkipSpace(original, i + 1);
                if (next >= original.Length || original[next] is not ((byte)'}' or (byte)']')) continue;
                if (trailing >= 0) { trailing = -1; break; }
                trailing = i;
            }
        }
        if (trailing >= 0)
        {
            var proposed = Replace(original, trailing, trailing + 1, []);
            if (Validate(proposed, out _) is null)
                return (Scope.Comma, proposed, null, null);
        }
        if (InsertMissingComma(original) is { } inserted)
            return (Scope.Comma, inserted, null, null);

        (Scope, byte[], string?, string?)? whole = null;
        var section = BrokenSection(original);
        foreach (var candidate in Backups(path))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var bytes = Read(candidate);
                if (Validate(bytes, out _) is not null)
                    continue;
                using var doc = JsonDocument.Parse(Content(bytes), JsonOptions);
                var root = doc.RootElement;
                if (!Compatible(path, root))
                    continue;
                if (section is { } region && root.TryGetProperty(region.Key, out var backupValue) &&
                    backupValue.ValueKind == (original[region.Start] == '{' ? JsonValueKind.Object : JsonValueKind.Array))
                {
                    var value = Encoding.UTF8.GetBytes(backupValue.GetRawText());
                    var proposed = Replace(original, region.Start, region.End, value);
                    if (Validate(proposed, out _) is null)
                    {
                        using var repaired = JsonDocument.Parse(Content(proposed), JsonOptions);
                        if (repaired.RootElement.EnumerateObject().Count(p => p.NameEquals(region.Key)) == 1)
                            return (Scope.Section, proposed, candidate, Hash(bytes));
                    }
                }
                whole ??= (Scope.WholeFile, bytes, candidate, Hash(bytes));
            }
            catch (Exception ex) when (ReadError(ex) || ex is JsonException) { }
        }
        return whole;
    }

    private static byte[]? InsertMissingComma(byte[] original)
    {
        var content = Content(original);
        try
        {
            using var alreadyValid = JsonDocument.Parse(content, JsonOptions);
            return null;
        }
        catch (JsonException error)
        {
            var bytes = content.Span;
            var start = 0;
            for (long line = 0; line < (error.LineNumber ?? 0); line++)
            {
                var newline = bytes[start..].IndexOf((byte)'\n');
                if (newline < 0) return null;
                start += newline + 1;
            }
            var fault = start + (int)(error.BytePositionInLine ?? 0);
            if (fault <= 0 || fault >= bytes.Length) return null;
            var next = SkipSpace(bytes, fault);
            var prior = next - 1;
            while (prior >= 0 && White(bytes[prior])) prior--;
            if (prior < 0 || next >= bytes.Length) return null;
            var right = bytes[next];
            var left = bytes[prior];
            // An adjacent digit in a malformed number such as 01 is ambiguous; never split it.
            if (prior + 1 == next && left is not ((byte)'}' or (byte)']' or (byte)'"') &&
                right is not ((byte)'{' or (byte)'[' or (byte)'"'))
                return null;
            if (left is (byte)'{' or (byte)'[' or (byte)',' or (byte)':' ||
                right is (byte)'}' or (byte)']' or (byte)',' or (byte)':')
                return null;
            var offset = prior + 1 + original.Length - bytes.Length;
            var proposed = Replace(original, offset, offset, ","u8);
            return Validate(proposed, out _) is null ? proposed : null;
        }
    }

    private static bool Compatible(string target, JsonElement document)
    {
        // Match the destination's exact backup name and a recognizable field with its expected type.
        // The two documents have different schemas. Do not restore an empty or unrelated JSON object.
        var state = Path.GetFileName(target) == ".claude.json";
        var names = new HashSet<string>(StringComparer.Ordinal);
        var recognized = false;
        foreach (var property in document.EnumerateObject())
        {
            if (!names.Add(property.Name)) return false;
            var type = property.Value.ValueKind;
            recognized |= state ? property.Name switch
            {
                "projects" or "mcpServers" or "oauthAccount" or "customApiKeyResponses" => type == JsonValueKind.Object,
                "installMethod" or "userID" or "lastVersion" => type == JsonValueKind.String,
                "numStartups" => type == JsonValueKind.Number,
                "hasCompletedOnboarding" => type is JsonValueKind.True or JsonValueKind.False,
                _ => false
            } : property.Name switch
            {
                "permissions" or "hooks" or "env" or "enabledPlugins" or "statusLine" or "sandbox" => type == JsonValueKind.Object,
                "model" or "language" or "theme" or "effortLevel" => type == JsonValueKind.String,
                "alwaysThinkingEnabled" => type is JsonValueKind.True or JsonValueKind.False,
                "$schema" => type == JsonValueKind.String &&
                    property.Value.ValueEquals("https://json.schemastore.org/claude-code-settings.json"),
                _ => false
            };
        }
        return recognized;
    }

    private static IEnumerable<string> Backups(string path)
    {
        var baseName = Path.GetFileName(path);
        var candidates = new List<FileInfo>();
        foreach (var folder in new[] { Path.GetDirectoryName(path)!, Path.Combine(Profile, ".claude", "backups") })
        {
            try
            {
                if (!string.Equals(folder, Profile, StringComparison.OrdinalIgnoreCase))
                    CheckPath(folder);
                foreach (var file in new DirectoryInfo(folder).EnumerateFiles(baseName + ".*"))
                {
                    if ((file.Attributes & FileAttributes.ReparsePoint) != 0 ||
                        !file.Name.StartsWith(baseName + ".", StringComparison.OrdinalIgnoreCase))
                        continue;
                    var extension = file.Name[(baseName.Length + 1)..];
                    var split = extension.IndexOfAny(['.', '-']);
                    var kind = split < 0 ? extension : extension[..split];
                    if (!kind.Equals("backup", StringComparison.OrdinalIgnoreCase) && !kind.Equals("bak", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (split >= 0 && (extension[(split + 1)..] is not { Length: > 0 and < 50 } stamp ||
                        !stamp.Any(char.IsAsciiDigit) || !stamp.All(c => char.IsAsciiDigit(c) || c is '-' or '_' or '.' or 'T' or 'Z')))
                        continue;
                    candidates.Add(file);
                }
            }
            catch (Exception ex) when (ReadError(ex)) { }
        }
        return candidates.OrderByDescending(file => file.LastWriteTimeUtc).ThenBy(file => file.FullName, StringComparer.OrdinalIgnoreCase)
            .Select(file => file.FullName);
    }

    private static (string Key, int Start, int End)? BrokenSection(byte[] file)
    {
        var raw = Content(file).Span;
        var reader = new Utf8JsonReader(raw, new JsonReaderOptions { MaxDepth = JsonOptions.MaxDepth });
        string? name = null;
        var start = -1;
        try
        {
            while (reader.Read())
            {
                if (reader.CurrentDepth != 1 || reader.TokenType != JsonTokenType.PropertyName)
                    continue;
                name = reader.GetString();
                start = SkipSpace(raw, (int)reader.BytesConsumed);
            }
        }
        catch (JsonException) { }
        catch (InvalidOperationException) { return null; }
        // Only replace a complete, balanced object or array whose own syntax is broken. The
        // rest of the file is preserved byte for byte, including adjacent fields and unknown keys.
        if (name is null || start < 0 || start >= raw.Length || raw[start] is not ((byte)'{' or (byte)'['))
            return null;
        Span<byte> stack = stackalloc byte[512];
        var depth = 0;
        var quoted = false;
        var escaped = false;
        for (var i = start; i < raw.Length; i++)
        {
            var b = raw[i];
            if (quoted)
            {
                if (escaped) escaped = false;
                else if (b == '\\') escaped = true;
                else if (b == '"') quoted = false;
            }
            else if (b == '"') quoted = true;
            else if (b is (byte)'{' or (byte)'[')
            {
                if (depth == stack.Length) return null;
                stack[depth++] = b == '{' ? (byte)'}' : (byte)']';
            }
            else if (b is (byte)'}' or (byte)']')
            {
                if (depth == 0 || stack[--depth] != b) return null;
                if (depth != 0) continue;
                try
                {
                    using var intact = JsonDocument.Parse(Content(file)[start..(i + 1)], JsonOptions);
                    return null;
                }
                catch (JsonException)
                {
                    var bom = file.Length - raw.Length;
                    return (name, start + bom, i + 1 + bom);
                }
            }
        }
        return null;
    }

    private static byte[] Replace(byte[] original, int start, int end, ReadOnlySpan<byte> value)
    {
        var size = original.Length - (end - start) + value.Length;
        if (size > MaxBytes) throw new IOException("Replacement exceeds inspection limit.");
        var output = new byte[size];
        original.AsSpan(0, start).CopyTo(output);
        value.CopyTo(output.AsSpan(start));
        original.AsSpan(end).CopyTo(output.AsSpan(start + value.Length));
        return output;
    }

    private static string RedactedDiff(byte[] before, byte[] after)
    {
        var left = Content(before).Span;
        var right = Content(after).Span;
        var prefix = 0;
        while (prefix < left.Length && prefix < right.Length && left[prefix] == right[prefix]) prefix++;
        var oldEnd = left.Length;
        var newEnd = right.Length;
        while (oldEnd > prefix && newEnd > prefix && left[oldEnd - 1] == right[newEnd - 1]) { oldEnd--; newEnd--; }
        return DrawRange("−", left, prefix, oldEnd) + Environment.NewLine + DrawRange("+", right, prefix, newEnd);
    }

    private static string DrawRange(string side, ReadOnlySpan<byte> bytes, int first, int end)
    {
        var point = Location(bytes, first);
        var last = Location(bytes, end);
        var lines = point.Line == last.Line ? $"{point.Line}" : $"{point.Line}–{last.Line}";
        // Print punctuation and placeholders only; even property names or malformed bare values
        // can carry credentials. Limit the excerpt around the first differing byte of a long line.
        var start = Math.Max(bytes[..first].LastIndexOf((byte)'\n') + 1, first - 45);
        var part = bytes[start..Math.Min(bytes.Length, first + 100)];
        var buffer = new StringBuilder();
        var hidden = false;
        var quoted = false;
        var escaped = false;
        foreach (var b in part)
        {
            if (b == '\n') break;
            if (quoted)
            {
                if (escaped) escaped = false;
                else if (b == '\\') escaped = true;
                else if (b == '"') { quoted = false; buffer.Append('"'); }
                continue;
            }
            if (b == '"') { quoted = true; buffer.Append("\"•••"); hidden = false; continue; }
            if (b is (byte)'{' or (byte)'}' or (byte)'[' or (byte)']' or (byte)',' or (byte)':')
            {
                buffer.Append((char)b);
                hidden = false;
            }
            else if (White(b)) { if (buffer.Length > 0 && buffer[^1] != ' ') buffer.Append(' '); }
            else if (!hidden) { buffer.Append('•'); hidden = true; }
        }
        return $"{side} {lines}  │  {buffer}";
    }
}
