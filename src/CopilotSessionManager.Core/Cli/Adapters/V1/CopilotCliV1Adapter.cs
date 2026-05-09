using System.Text.Json;
using CopilotSessionManager.Core.Models;
using Microsoft.Extensions.Logging;

namespace CopilotSessionManager.Core.Cli.Adapters.V1;

/// <summary>
/// Adapter for Copilot CLI 1.x. Implementations should be conservative:
/// unknown event types are passed through verbatim, and missing fields default
/// to null rather than throwing. See ADR-0003.
/// </summary>
public sealed class CopilotCliV1Adapter : ICopilotCliAdapter
{
    private static readonly CopilotVersion Min = new(1, 0, 0);
    private static readonly CopilotVersion Max = new(1, int.MaxValue, int.MaxValue);

    private readonly EventsJsonlReader _eventsReader;
    private readonly WorkspaceYamlReader _workspaceReader;

    public CopilotCliV1Adapter(ILogger<CopilotCliV1Adapter> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _eventsReader = new EventsJsonlReader(logger);
        _workspaceReader = new WorkspaceYamlReader();
    }

    public CopilotVersion MinSupported => Min;

    public CopilotVersion MaxSupported => Max;

    public bool Supports(CopilotVersion version) =>
        version >= Min && version <= Max;

    public async Task<CopilotVersion?> ReadCopilotVersionAsync(
        Stream eventsJsonl,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventsJsonl);

        await foreach (var ev in ParseEventsAsync(eventsJsonl, cancellationToken))
        {
            if (!string.Equals(ev.Type, "session.start", StringComparison.Ordinal))
            {
                continue;
            }

            if (ev.Data is { } data &&
                data.ValueKind == JsonValueKind.Object &&
                data.TryGetProperty("copilotVersion", out var versionEl) &&
                versionEl.ValueKind == JsonValueKind.String &&
                CopilotVersion.TryParse(versionEl.GetString(), out var version))
            {
                return version;
            }

            // First session.start had no usable version — stop looking.
            return null;
        }

        return null;
    }

    public IAsyncEnumerable<SessionEvent> ParseEventsAsync(
        Stream eventsJsonl,
        CancellationToken cancellationToken = default) =>
        _eventsReader.ReadAsync(eventsJsonl, cancellationToken);

    public WorkspaceManifest ParseWorkspace(string yaml) =>
        _workspaceReader.Parse(yaml);
}
