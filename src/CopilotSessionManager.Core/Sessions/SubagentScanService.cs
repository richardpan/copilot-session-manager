using System.Text.Json;
using CopilotSessionManager.Core.Cli.Adapters.V1;
using CopilotSessionManager.Core.Models;
using Microsoft.Extensions.Logging;

namespace CopilotSessionManager.Core.Sessions;

public sealed class SubagentScanService : ISubagentScanService
{
    private readonly ICopilotPaths _paths;
    private readonly ILogger<SubagentScanService> _logger;
    private readonly EventsJsonlReader _events;

    public SubagentScanService(ICopilotPaths paths, ILogger<SubagentScanService> logger)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(logger);

        _paths = paths;
        _logger = logger;
        _events = new EventsJsonlReader(logger);
    }

    public async Task<IReadOnlyList<SubagentSummary>> ScanAsync(string sessionId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ct.ThrowIfCancellationRequested();

        var path = Path.Combine(_paths.SessionStateDirectory, sessionId, "events.jsonl");
        if (!File.Exists(path))
        {
            return Array.Empty<SubagentSummary>();
        }

        var builders = new Dictionary<string, Builder>(StringComparer.Ordinal);
        try
        {
            await using var stream = new FileStream(path, new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.ReadWrite | FileShare.Delete,
                BufferSize = 64 * 1024,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            });

            await foreach (var ev in _events.ReadAsync(stream, ct).ConfigureAwait(false))
            {
                if (ev.Data is not { ValueKind: JsonValueKind.Object } data)
                {
                    continue;
                }

                switch (ev.Type)
                {
                    case "tool.execution_start":
                        ReadToolExecutionStart(data, builders);
                        break;
                    case "subagent.started":
                        ReadSubagentStarted(ev, data, builders);
                        break;
                    case "subagent.completed":
                        ReadSubagentCompleted(ev, data, builders);
                        break;
                }
            }
        }
        catch (FileNotFoundException)
        {
            return Array.Empty<SubagentSummary>();
        }
        catch (DirectoryNotFoundException)
        {
            return Array.Empty<SubagentSummary>();
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(ex, "Skipping malformed sub-agent event payload in {Path}.", path);
        }

        var summaries = new List<SubagentSummary>();
        foreach (var builder in builders.Values)
        {
            if (!builder.HasStarted)
            {
                _logger.LogDebug(
                    "Skipping task tool call {ToolCallId} in session {SessionId}: missing subagent.started event.",
                    builder.ToolCallId,
                    sessionId);
                continue;
            }

            summaries.Add(builder.ToSummary());
        }

        return summaries
            .OrderBy(static s => s.StartedAt)
            .ThenBy(static s => s.ToolCallId, StringComparer.Ordinal)
            .ToArray();
    }

    private void ReadToolExecutionStart(JsonElement data, Dictionary<string, Builder> builders)
    {
        if (!string.Equals(ReadString(data, "toolName"), "task", StringComparison.Ordinal))
        {
            return;
        }

        var toolCallId = ReadString(data, "toolCallId");
        if (string.IsNullOrWhiteSpace(toolCallId))
        {
            _logger.LogDebug("Skipping task tool.execution_start without toolCallId.");
            return;
        }

        var name = "task";
        var agentType = "unknown";
        if (data.TryGetProperty("arguments", out var args) && args.ValueKind == JsonValueKind.Object)
        {
            name = NonEmpty(ReadString(args, "name"), name);
            agentType = NonEmpty(ReadString(args, "agent_type"), agentType);
        }

        builders[toolCallId] = new Builder(toolCallId)
        {
            Name = name,
            AgentType = agentType,
        };
    }

    private void ReadSubagentStarted(SessionEvent ev, JsonElement data, Dictionary<string, Builder> builders)
    {
        var agentId = ReadString(data, "agentId");
        if (string.IsNullOrWhiteSpace(agentId))
        {
            _logger.LogDebug("Skipping subagent.started without agentId.");
            return;
        }

        if (!builders.TryGetValue(agentId, out var builder))
        {
            _logger.LogDebug("Skipping subagent.started for unknown task tool call {ToolCallId}.", agentId);
            return;
        }

        builder.AgentDisplayName = ReadString(data, "agentDisplayName");
        builder.StartedAt = ev.Timestamp;
        builder.HasStarted = true;
    }

    private void ReadSubagentCompleted(SessionEvent ev, JsonElement data, Dictionary<string, Builder> builders)
    {
        var agentId = ReadString(data, "agentId");
        if (string.IsNullOrWhiteSpace(agentId))
        {
            _logger.LogDebug("Skipping subagent.completed without agentId.");
            return;
        }

        if (!builders.TryGetValue(agentId, out var builder))
        {
            _logger.LogDebug("Skipping subagent.completed for unknown task tool call {ToolCallId}.", agentId);
            return;
        }

        builder.TokensTotal = ReadLong(data, "totalTokens");
        builder.ToolCallsTotal = ReadInt(data, "totalToolCalls");
        var durationMs = ReadLong(data, "durationMs");
        builder.Duration = durationMs > 0
            ? TimeSpan.FromMilliseconds(durationMs)
            : null;
        builder.Model = ReadString(data, "model");
        builder.CompletedAt = ev.Timestamp;
        builder.Status = IsCancelled(data) ? SubagentStatus.Cancelled : SubagentStatus.Completed;
    }

    private static string NonEmpty(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value!;

    private static string? ReadString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long ReadLong(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt64(out var result)
            ? result
            : 0;

    private static int ReadInt(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out var result)
            ? result
            : 0;

    private static bool IsCancelled(JsonElement data) =>
        string.Equals(ReadString(data, "status"), "cancelled", StringComparison.OrdinalIgnoreCase) ||
        data.TryGetProperty("cancelled", out var cancelled) &&
        cancelled.ValueKind is JsonValueKind.True;

    private sealed class Builder(string toolCallId)
    {
        public string ToolCallId { get; } = toolCallId;
        public string Name { get; init; } = "task";
        public string AgentType { get; init; } = "unknown";
        public string? AgentDisplayName { get; set; }
        public string? Model { get; set; }
        public long TokensTotal { get; set; }
        public int ToolCallsTotal { get; set; }
        public TimeSpan? Duration { get; set; }
        public DateTimeOffset StartedAt { get; set; }
        public DateTimeOffset? CompletedAt { get; set; }
        public SubagentStatus Status { get; set; } = SubagentStatus.Running;
        public bool HasStarted { get; set; }

        public SubagentSummary ToSummary() => new(
            ToolCallId,
            Name,
            AgentType,
            AgentDisplayName,
            Model,
            TokensTotal,
            ToolCallsTotal,
            Duration,
            StartedAt,
            CompletedAt,
            CompletedAt is null ? SubagentStatus.Running : Status);
    }
}
