using System.Text.Json;
using CopilotSessionManager.Core.Cli.Adapters.V1;
using CopilotSessionManager.Core.Models;
using Microsoft.Extensions.Logging;

namespace CopilotSessionManager.Core.Sessions;

/// <summary>
/// Default <see cref="ISessionEventSummaryService"/>. Streams
/// <c>events.jsonl</c> using the same pattern as
/// <see cref="SubagentScanService"/> (file-share-friendly, async, sequential
/// scan) and aggregates the data the README renderer needs without ever
/// holding the full file in memory.
/// </summary>
public sealed class SessionEventSummaryService : ISessionEventSummaryService
{
    private readonly ICopilotPaths _paths;
    private readonly ILogger<SessionEventSummaryService> _logger;
    private readonly EventsJsonlReader _events;

    public SessionEventSummaryService(ICopilotPaths paths, ILogger<SessionEventSummaryService> logger)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(logger);

        _paths = paths;
        _logger = logger;
        _events = new EventsJsonlReader(logger);
    }

    public async Task<SessionEventSummary> ScanAsync(string sessionId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ct.ThrowIfCancellationRequested();

        var path = Path.Combine(_paths.SessionStateDirectory, sessionId, "events.jsonl");
        if (!File.Exists(path))
        {
            return SessionEventSummary.Empty;
        }

        // Bounded ring of recent user prompts so we never grow past MaxRecentPrompts.
        var recent = new Queue<RecentPrompt>(SessionEventSummary.MaxRecentPrompts);
        var toolCounts = new Dictionary<string, int>(StringComparer.Ordinal);

        DateTimeOffset? firstTs = null;
        DateTimeOffset? lastTs = null;
        TimeSpan? longestGap = null;
        var total = 0;

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
                total++;

                // Activity span / gap tracking — every event participates,
                // not just user.message / tool.execution_start, so we get a
                // realistic picture of when the session was idle.
                if (ev.Timestamp != DateTimeOffset.MinValue)
                {
                    if (firstTs is null)
                    {
                        firstTs = ev.Timestamp;
                    }
                    else if (lastTs is { } prev)
                    {
                        var gap = ev.Timestamp - prev;
                        if (gap > TimeSpan.Zero && (longestGap is null || gap > longestGap))
                        {
                            longestGap = gap;
                        }
                    }
                    lastTs = ev.Timestamp;
                }

                if (ev.Data is not { ValueKind: JsonValueKind.Object } data)
                {
                    continue;
                }

                switch (ev.Type)
                {
                    case "user.message":
                        var body = ReadString(data, "content");
                        if (!string.IsNullOrWhiteSpace(body))
                        {
                            // Bounded queue: enqueue, then trim oldest.
                            recent.Enqueue(new RecentPrompt(ev.Timestamp, NormalizeBody(body!)));
                            while (recent.Count > SessionEventSummary.MaxRecentPrompts)
                            {
                                recent.Dequeue();
                            }
                        }
                        break;

                    case "tool.execution_start":
                        var toolName = ReadString(data, "toolName");
                        if (!string.IsNullOrWhiteSpace(toolName))
                        {
                            toolCounts.TryGetValue(toolName!, out var c);
                            toolCounts[toolName!] = c + 1;
                        }
                        break;
                }
            }
        }
        catch (FileNotFoundException)
        {
            return SessionEventSummary.Empty;
        }
        catch (DirectoryNotFoundException)
        {
            return SessionEventSummary.Empty;
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(ex, "Skipping malformed event payload while summarising {Path}.", path);
        }

        TimeSpan? span = (firstTs is { } a && lastTs is { } b && b > a)
            ? b - a
            : null;

        // RecentPrompts: newest-first per the contract on SessionEventSummary.
        var recentList = recent.Reverse().ToArray();

        var topTools = toolCounts
            .OrderByDescending(static kv => kv.Value)
            .ThenBy(static kv => kv.Key, StringComparer.Ordinal)
            .Take(SessionEventSummary.MaxTopTools)
            .Select(static kv => new ToolUsageCount(kv.Key, kv.Value))
            .ToArray();

        return new SessionEventSummary(
            recentList,
            topTools,
            longestGap,
            span,
            total);
    }

    private static string NormalizeBody(string body)
    {
        // Collapse line breaks so the prompt fits on a single markdown bullet.
        var collapsed = body
            .Replace("\r\n", " ", StringComparison.Ordinal)
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();

        if (collapsed.Length <= SessionEventSummary.MaxPromptBodyChars)
        {
            return collapsed;
        }

        return collapsed[..SessionEventSummary.MaxPromptBodyChars] + "…";
    }

    private static string? ReadString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
