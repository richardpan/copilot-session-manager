using System.Text.Json;
using CopilotSessionManager.Core.Models;
using Microsoft.Extensions.Logging;

namespace CopilotSessionManager.Core.Cli.Adapters.V1;

/// <summary>
/// Walks an <c>events.jsonl</c> stream and produces a
/// <see cref="SessionModelInfo"/> snapshot.
///
/// <para>
/// Resolution order, in priority:
/// </para>
/// <list type="number">
///   <item><c>session.shutdown</c> — authoritative; provides
///   <c>currentModel</c> + per-model <c>modelMetrics</c> (token totals,
///   request counts).</item>
///   <item>The most recent <c>tool.execution_complete</c> with a
///   <c>data.model</c> string — gives us a current model for active sessions
///   without tokens.</item>
///   <item><c>session.start</c> with <c>data.selectedModel</c> — first
///   resort. Captured along the way for the fallback case.</item>
/// </list>
///
/// Returns <see cref="SessionModelInfo.Empty"/> when nothing usable is found.
/// </summary>
internal sealed class SessionModelInfoReader
{
    private readonly EventsJsonlReader _events;
    private readonly ILogger _logger;

    public SessionModelInfoReader(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        _events = new EventsJsonlReader(logger);
    }

    public async Task<SessionModelInfo> ReadAsync(
        Stream eventsJsonl,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventsJsonl);

        string? selectedModel = null;
        string? lastToolModel = null;
        string? lastAssistantModel = null;
        SessionModelInfo? shutdown = null;

        // Live tally accumulated for active sessions that haven't emitted a
        // session.shutdown event yet. Keyed by model id.
        var liveUsage = new Dictionary<string, MutableUsage>(StringComparer.Ordinal);

        await foreach (var ev in _events.ReadAsync(eventsJsonl, cancellationToken)
            .ConfigureAwait(false))
        {
            if (ev.Data is not { } data || data.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            switch (ev.Type)
            {
                case "session.start":
                    if (data.TryGetProperty("selectedModel", out var sm) &&
                        sm.ValueKind == JsonValueKind.String)
                    {
                        selectedModel = sm.GetString();
                    }
                    break;

                case "tool.execution_complete":
                    if (data.TryGetProperty("model", out var tm) &&
                        tm.ValueKind == JsonValueKind.String)
                    {
                        var v = tm.GetString();
                        if (!string.IsNullOrWhiteSpace(v))
                        {
                            lastToolModel = v;
                        }
                    }
                    break;

                case "assistant.message":
                    AccumulateAssistantMessage(data, liveUsage, ref lastAssistantModel);
                    break;

                case "session.compaction_complete":
                    AccumulateCompaction(data, liveUsage);
                    break;

                case "session.shutdown":
                    // Last shutdown wins (sessions sometimes restart); keep
                    // walking so we always pick the freshest one.
                    shutdown = ParseShutdown(data) ?? shutdown;
                    break;
            }
        }

        if (shutdown is not null)
        {
            return shutdown;
        }

        var current = lastAssistantModel ?? lastToolModel ?? selectedModel;
        if (current is null && liveUsage.Count == 0)
        {
            return SessionModelInfo.Empty;
        }

        var usage = new Dictionary<string, ModelUsage>(StringComparer.Ordinal);
        foreach (var kv in liveUsage)
        {
            usage[kv.Key] = kv.Value.ToImmutable();
        }

        // Fall back to whichever model carried the most requests if we still
        // don't have a "current" hint but did accumulate usage.
        if (current is null && usage.Count > 0)
        {
            current = usage.OrderByDescending(kv => kv.Value.RequestCount).First().Key;
        }

        return new SessionModelInfo(
            CurrentModelId: current,
            IsFromShutdown: false,
            UsageByModel: usage);
    }

    private static void AccumulateAssistantMessage(
        JsonElement data,
        Dictionary<string, MutableUsage> liveUsage,
        ref string? lastAssistantModel)
    {
        if (!data.TryGetProperty("model", out var modelEl) ||
            modelEl.ValueKind != JsonValueKind.String)
        {
            return;
        }
        var model = modelEl.GetString();
        if (string.IsNullOrWhiteSpace(model))
        {
            return;
        }
        lastAssistantModel = model;

        // Every assistant.message represents a model invocation, so always
        // bump the request count. outputTokens is optional (Copilot only
        // emits it on the final chunk of a streamed reply).
        var bucket = GetOrCreate(liveUsage, model);
        bucket.RequestCount += 1;

        if (data.TryGetProperty("outputTokens", out var ot) &&
            ot.ValueKind == JsonValueKind.Number &&
            ot.TryGetInt64(out var outputTokens) &&
            outputTokens > 0)
        {
            bucket.OutputTokens += outputTokens;
        }
    }

    private static void AccumulateCompaction(
        JsonElement data,
        Dictionary<string, MutableUsage> liveUsage)
    {
        if (!data.TryGetProperty("compactionTokensUsed", out var usage) ||
            usage.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var model = usage.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String
            ? m.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(model))
        {
            return;
        }

        var bucket = GetOrCreate(liveUsage, model);
        bucket.InputTokens += ReadLong(usage, "inputTokens");
        bucket.OutputTokens += ReadLong(usage, "outputTokens");
        bucket.CacheReadTokens += ReadLong(usage, "cacheReadTokens");
        bucket.CacheWriteTokens += ReadLong(usage, "cacheWriteTokens");
        bucket.ReasoningTokens += ReadLong(usage, "reasoningTokens");
    }

    private static MutableUsage GetOrCreate(Dictionary<string, MutableUsage> map, string model)
    {
        if (!map.TryGetValue(model, out var bucket))
        {
            bucket = new MutableUsage();
            map[model] = bucket;
        }
        return bucket;
    }

    private sealed class MutableUsage
    {
        public long InputTokens;
        public long OutputTokens;
        public long CacheReadTokens;
        public long CacheWriteTokens;
        public long ReasoningTokens;
        public int RequestCount;

        public ModelUsage ToImmutable() => new(
            InputTokens, OutputTokens, CacheReadTokens, CacheWriteTokens, ReasoningTokens, RequestCount);
    }

    private SessionModelInfo? ParseShutdown(JsonElement data)
    {
        try
        {
            string? current = data.TryGetProperty("currentModel", out var cm) &&
                cm.ValueKind == JsonValueKind.String
                ? cm.GetString()
                : null;

            var usage = new Dictionary<string, ModelUsage>(StringComparer.Ordinal);
            if (data.TryGetProperty("modelMetrics", out var metrics) &&
                metrics.ValueKind == JsonValueKind.Object)
            {
                foreach (var entry in metrics.EnumerateObject())
                {
                    if (entry.Value.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var u = ParseUsage(entry.Value);
                    if (u is not null)
                    {
                        usage[entry.Name] = u;
                    }
                }
            }

            // If the shutdown didn't name a current model but did report
            // metrics, fall back to the most-used one (max requests).
            if (current is null && usage.Count > 0)
            {
                current = usage.OrderByDescending(kv => kv.Value.RequestCount).First().Key;
            }

            return new SessionModelInfo(current, IsFromShutdown: true, usage);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            _logger.LogDebug(ex, "Skipping malformed session.shutdown event payload.");
            return null;
        }
    }

    private static ModelUsage? ParseUsage(JsonElement metric)
    {
        var requestCount = 0;
        if (metric.TryGetProperty("requests", out var req) &&
            req.ValueKind == JsonValueKind.Object &&
            req.TryGetProperty("count", out var c) &&
            c.ValueKind == JsonValueKind.Number &&
            c.TryGetInt32(out var rc))
        {
            requestCount = rc;
        }

        if (!metric.TryGetProperty("usage", out var usage) ||
            usage.ValueKind != JsonValueKind.Object)
        {
            return new ModelUsage(0, 0, 0, 0, 0, requestCount);
        }

        return new ModelUsage(
            InputTokens: ReadLong(usage, "inputTokens"),
            OutputTokens: ReadLong(usage, "outputTokens"),
            CacheReadTokens: ReadLong(usage, "cacheReadTokens"),
            CacheWriteTokens: ReadLong(usage, "cacheWriteTokens"),
            ReasoningTokens: ReadLong(usage, "reasoningTokens"),
            RequestCount: requestCount);
    }

    private static long ReadLong(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) &&
        v.ValueKind == JsonValueKind.Number &&
        v.TryGetInt64(out var l)
            ? l
            : 0;
}
