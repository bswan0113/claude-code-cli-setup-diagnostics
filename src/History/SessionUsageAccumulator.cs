using System.Text.Json;

namespace ClaudeLauncher;

internal sealed class SessionUsageAccumulator(Guid sessionId)
{
    private readonly Dictionary<string, Response> _aliases = new(StringComparer.Ordinal);
    private readonly HashSet<Response> _responses = [];
    private readonly HashSet<string> _boundaries = new(StringComparer.Ordinal);
    private int _compactions;
    private bool _partial;
    private Response? _latest;
    private long? _context, _capacity;
    private double? _percentage;
    private string? _model, _requestedModel;

    internal void MarkGap()
    {
        _partial = true;
        _latest = null;
        _context = _capacity = null;
        _percentage = null;
    }

    internal void Observe(JsonElement row)
    {
        if (row.ValueKind != JsonValueKind.Object || True(row, "isSidechain") ||
            (row.TryGetProperty("sessionId", out var id) &&
             !MatchesSession(id)) || (row.TryGetProperty("session_id", out var sid) && !MatchesSession(sid)))
            return;
        var type = Text(row, "type");
        if (type == "system" && Text(row, "subtype") == "compact_boundary")
        {
            var uuid = Text(row, "uuid");
            if (uuid is not null && !_boundaries.Add(uuid))
                return;
            _compactions++;
            _latest = null;
            _context = Count(Child(row, "compactMetadata"), "postTokens");
            _percentage = null;
            return;
        }
        if (type == "assistant")
            ObserveAssistant(row);
        // Only trust telemetry envelopes at the root, never tool payloads or arbitrary message text.
        if (type is "assistant" or "system" or "status" or "statusline" or "result" ||
            type is null && row.TryGetProperty("session_id", out _))
            ObserveWindow(Child(row, "context_window"));
    }

    private bool MatchesSession(JsonElement id) =>
        id.ValueKind == JsonValueKind.String && id.TryGetGuid(out var actual) && actual == sessionId;

    private void ObserveAssistant(JsonElement row)
    {
        var message = Child(row, "message");
        if (message.ValueKind != JsonValueKind.Object || True(row, "isApiErrorMessage"))
            return;
        var model = Text(message, "model");
        if (model == "<synthetic>" || Text(message, "role") is { } role && role != "assistant")
            return;

        var usage = Child(message, "usage");
        var values = Counts.From(usage);
        var request = Text(row, "requestId");
        var messageId = Text(message, "id");
        if (messageId is null && request is null)
        {
            // Distinct JSONL UUIDs can be blocks of the same API reply. A row UUID alone
            // cannot establish a billable response; don't silently sum ambiguous blocks.
            MarkGap();
            SetLatest(values, model, Text(row, "requestedModel"), null);
            return;
        }

        var messageKey = messageId is null ? null : "m:" + messageId;
        var requestKey = request is null ? null : "r:" + request;
        var fromMessage = messageKey is null ? null : _aliases.GetValueOrDefault(messageKey);
        var fromRequest = requestKey is null ? null : _aliases.GetValueOrDefault(requestKey);
        var response = fromMessage ?? fromRequest ?? new Response();
        var fresh = _responses.Add(response);
        if (fromMessage is not null && fromRequest is not null && fromMessage != fromRequest)
        {
            foreach (var alias in fromRequest.Aliases)
                Bind(alias, response);
            response.Values = response.Values.Merge(fromRequest.Values);
            if (_latest == fromRequest)
                _latest = response;
            _responses.Remove(fromRequest);
        }
        Bind(messageKey, response);
        Bind(requestKey, response);
        response.Values = response.Values.Merge(values);
        if (fresh || response == _latest)
            SetLatest(response.Values, model, Text(row, "requestedModel"), response);
    }

    private void Bind(string? key, Response response)
    {
        if (key is not null && _aliases.GetValueOrDefault(key) != response)
        {
            _aliases[key] = response;
            response.Aliases.Add(key);
        }
    }

    private void SetLatest(Counts counts, string? model, string? requested, Response? response)
    {
        if (model != _model || requested != _requestedModel)
            _capacity = null;
        _model = model;
        _requestedModel = requested;
        _latest = response;
        _context = counts.Input;
        _percentage = null;
    }

    private void ObserveWindow(JsonElement window)
    {
        if (window.ValueKind != JsonValueKind.Object)
            return;
        if (Count(window, "context_window_size") is long capacity && capacity > 0)
            _capacity = capacity;
        // current_usage is the last API call. Older status-line exporters used cumulative totals,
        // so a lone total_input_tokens field cannot establish the size of the current context.
        _context = Counts.From(Child(window, "current_usage")).Input ?? _context;
        if (window.TryGetProperty("used_percentage", out var used) && used.ValueKind == JsonValueKind.Number &&
            used.TryGetDouble(out var percentage) && percentage is >= 0 and <= 100)
            _percentage = percentage;
    }

    internal SessionUsage Snapshot()
    {
        var partial = _partial;
        long? total = null;
        try
        {
            foreach (var response in _responses)
            {
                var counts = response.Values;
                partial |= !counts.Complete;
                if (counts.Input.HasValue || counts.Output.HasValue)
                    total = checked((total ?? 0) + (counts.Input ?? 0) + (counts.Output ?? 0));
            }
        }
        catch (OverflowException)
        {
            total = null;
            partial = true;
        }
        var capacity = _capacity ?? (_percentage.HasValue ? null : EstimatedCapacity(_model, _requestedModel));
        if (_capacity is null && _context > capacity)
            capacity = null; // The observed usage already rules out the inferred default.
        var percent = _percentage ?? (_context.HasValue && capacity is > 0 && _context <= capacity
            ? 100d * _context.Value / capacity.Value : (double?)null);
        return new(total, _context, capacity, percent, _partial && _compactions == 0 ? null : _compactions, partial,
            _capacity is null && capacity.HasValue && _percentage is null, _model);
    }

    // These are model defaults, not account/session limits. A provider or Claude Code setting
    // can override them. Unknown IDs stay unknown. https://code.claude.com/docs/en/model-config
    private static long? EstimatedCapacity(string? model, string? requested)
    {
        if (requested?.EndsWith("[1m]", StringComparison.OrdinalIgnoreCase) == true ||
            model?.EndsWith("[1m]", StringComparison.OrdinalIgnoreCase) == true)
            return 1_000_000;
        if (model is null)
            return null;
        // Provider snapshots may append a calendar date to an otherwise identical model ID.
        var lastDash = model.LastIndexOf('-');
        if (lastDash >= 0 && model.Length - lastDash == 9 && model.AsSpan(lastDash + 1).IndexOfAnyExceptInRange('0', '9') < 0)
            model = model[..lastDash];
        return model switch
        {
            "claude-opus-4-7" or "claude-opus-4-8" or "claude-opus-5" or "claude-opus-5-5" or
            "claude-sonnet-5" or "claude-sonnet-5-5" or
            "claude-haiku-5-5" or "claude-fable-5" or "claude-fable-5-1" or "claude-fable-5-2" => 1_000_000,
            "claude-opus-4" or "claude-opus-4-1" or "claude-opus-4-5" or "claude-opus-4-6" or
            "claude-sonnet-4" or "claude-sonnet-4-5" or "claude-sonnet-4-6" or "claude-haiku-4-5" => 200_000,
            _ => null
        };
    }

    private sealed class Response
    {
        public List<string> Aliases { get; } = [];
        public Counts Values;
    }

    private readonly record struct Counts(long? Raw, long? Created, long? Read, long? Output, long? Inclusive)
    {
        internal static Counts From(JsonElement value) => new(Count(value, "input_tokens"),
            Count(value, "cache_creation_input_tokens"), Count(value, "cache_read_input_tokens"),
            Count(value, "output_tokens"), Count(value, "total_input_tokens"));

        // Streaming block snapshots can repeat and can leave fields out. Output is cumulative
        // within the response. Cache creation details split the creation total and are never added.
        internal Counts Merge(Counts next) => new(next.Raw ?? Raw, next.Created ?? Created, next.Read ?? Read,
            Output is { } prior && next.Output is { } current ? Math.Max(prior, current) : next.Output ?? Output,
            next.Inclusive ?? Inclusive);

        internal long? Input
        {
            get
            {
                if (Inclusive.HasValue || !Raw.HasValue)
                    return Inclusive;
                var raw = Raw.Value;
                var cached = Created ?? 0;
                var read = Read ?? 0;
                return raw <= long.MaxValue - cached && raw + cached <= long.MaxValue - read ? raw + cached + read : null;
            }
        }
        internal bool Complete => Output.HasValue && Input.HasValue &&
            (Inclusive.HasValue || Created.HasValue && Read.HasValue);
    }

    private static JsonElement Child(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var child) ? child : default;
    private static bool True(JsonElement value, string name) => Child(value, name).ValueKind == JsonValueKind.True;
    private static string? Text(JsonElement value, string name) =>
        Child(value, name) is { ValueKind: JsonValueKind.String } text && text.GetString() is { Length: > 0 and < 256 } str ? str : null;
    private static long? Count(JsonElement value, string name) =>
        Child(value, name) is { ValueKind: JsonValueKind.Number } number && number.TryGetInt64(out var result) && result >= 0
            ? result : null;
}
