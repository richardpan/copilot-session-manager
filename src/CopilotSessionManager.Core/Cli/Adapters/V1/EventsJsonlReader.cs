using System.Runtime.CompilerServices;
using System.Text.Json;
using CopilotSessionManager.Core.Models;
using Microsoft.Extensions.Logging;

namespace CopilotSessionManager.Core.Cli.Adapters.V1;

/// <summary>
/// Streams events from a Copilot CLI <c>events.jsonl</c> stream, one JSON
/// object per line. Malformed lines are logged and skipped.
/// </summary>
internal sealed class EventsJsonlReader
{
    private static readonly JsonDocumentOptions DocOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    private readonly ILogger _logger;

    public EventsJsonlReader(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    public async IAsyncEnumerable<SessionEvent> ReadAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using var reader = new StreamReader(stream, leaveOpen: true);
        var lineNumber = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                yield break;
            }

            lineNumber++;

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            SessionEvent? parsed = null;
            try
            {
                parsed = ParseLine(line);
            }
            catch (JsonException ex)
            {
                _logger.LogDebug(
                    ex,
                    "Skipping malformed events.jsonl line {LineNumber}.",
                    lineNumber);
            }

            if (parsed is not null)
            {
                yield return parsed;
            }
        }
    }

    private static SessionEvent? ParseLine(string line)
    {
        using var doc = JsonDocument.Parse(line, DocOptions);

        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var id = TryGetString(root, "id") ?? string.Empty;
        var type = TryGetString(root, "type");
        if (string.IsNullOrEmpty(type))
        {
            return null;
        }

        var timestamp = TryGetTimestamp(root, "timestamp") ?? DateTimeOffset.MinValue;
        var parentId = TryGetString(root, "parentId");

        JsonElement? data = null;
        if (root.TryGetProperty("data", out var dataEl) &&
            dataEl.ValueKind != JsonValueKind.Undefined &&
            dataEl.ValueKind != JsonValueKind.Null)
        {
            data = dataEl.Clone();
        }

        return new SessionEvent(id, type!, timestamp, parentId, data);
    }

    private static string? TryGetString(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static DateTimeOffset? TryGetTimestamp(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return DateTimeOffset.TryParse(
            v.GetString(),
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
            out var ts)
            ? ts
            : null;
    }
}
