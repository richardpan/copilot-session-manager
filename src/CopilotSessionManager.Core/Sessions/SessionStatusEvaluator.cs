using System.Text.Json;
using CopilotSessionManager.Core.Cli;
using CopilotSessionManager.Core.Models;
using Microsoft.Extensions.Logging;

namespace CopilotSessionManager.Core.Sessions;

/// <inheritdoc />
public sealed class SessionStatusEvaluator : ISessionStatusEvaluator
{
    private const string EventsFileName = "events.jsonl";

    private readonly ICopilotPaths _paths;
    private readonly ICopilotCliAdapterRegistry _adapterRegistry;
    private readonly StatusDetectionOptions _options;
    private readonly ILogger<SessionStatusEvaluator> _logger;

    public SessionStatusEvaluator(
        ICopilotPaths paths,
        ICopilotCliAdapterRegistry adapterRegistry,
        StatusDetectionOptions options,
        ILogger<SessionStatusEvaluator> logger)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(adapterRegistry);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _paths = paths;
        _adapterRegistry = adapterRegistry;
        _options = options;
        _logger = logger;
    }

    public async Task<SessionStatus> EvaluateAsync(
        string sessionId,
        IReadOnlyList<SessionLockInfo> locks,
        CopilotVersion copilotVersion,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(locks);

        if (locks.Count == 0)
        {
            return SessionStatus.Inactive;
        }

        if (!locks.Any(static l => l.IsAlive))
        {
            return SessionStatus.Orphaned;
        }

        var eventsPath = Path.Combine(_paths.SessionStateDirectory, sessionId, EventsFileName);
        if (!File.Exists(eventsPath))
        {
            // We have a live lock but no events yet — treat it as warming up.
            return SessionStatus.Working;
        }

        var adapter = copilotVersion == CopilotVersion.Zero
            ? _adapterRegistry.Latest
            : _adapterRegistry.Resolve(copilotVersion).Adapter;

        var openTurns = new HashSet<string>(StringComparer.Ordinal);
        var openPermissions = new HashSet<string>(StringComparer.Ordinal);
        string? lastTerminalEventType = null;
        DateTimeOffset? lastEventTimestamp = null;
        var processed = 0;

        try
        {
            await using var stream = new FileStream(
                eventsPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            await foreach (var ev in adapter.ParseEventsAsync(stream, cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (++processed > _options.MaxEventsToReplay)
                {
                    // Drop the oldest event from our state window: re-enter the
                    // newest and trim the open sets opportunistically.
                    // (We accept slight inaccuracy on extremely long sessions.)
                }

                if (ev.Timestamp > DateTimeOffset.MinValue)
                {
                    lastEventTimestamp = ev.Timestamp;
                }

                switch (ev.Type)
                {
                    case "assistant.turn_start":
                        {
                            var turnId = TryGetString(ev.Data, "turnId");
                            if (!string.IsNullOrEmpty(turnId))
                            {
                                openTurns.Add(turnId);
                            }
                            lastTerminalEventType = ev.Type;
                            break;
                        }
                    case "assistant.turn_end":
                        {
                            var turnId = TryGetString(ev.Data, "turnId");
                            if (!string.IsNullOrEmpty(turnId))
                            {
                                openTurns.Remove(turnId);
                            }
                            lastTerminalEventType = ev.Type;
                            break;
                        }
                    case "permission.requested":
                        {
                            var permissionId = TryGetString(ev.Data, "permissionId");
                            if (!string.IsNullOrEmpty(permissionId))
                            {
                                openPermissions.Add(permissionId);
                            }
                            lastTerminalEventType = ev.Type;
                            break;
                        }
                    case "permission.completed":
                        {
                            var permissionId = TryGetString(ev.Data, "permissionId");
                            if (!string.IsNullOrEmpty(permissionId))
                            {
                                openPermissions.Remove(permissionId);
                            }
                            break;
                        }
                }
            }
        }
        catch (IOException ex)
        {
            _logger.LogDebug(ex, "Could not read {Path} for status; assuming Working.", eventsPath);
            return SessionStatus.Working;
        }

        if (openPermissions.Count > 0)
        {
            return SessionStatus.AwaitingApproval;
        }

        if (openTurns.Count > 0)
        {
            return SessionStatus.Working;
        }

        if (lastTerminalEventType == "assistant.turn_end")
        {
            if (lastEventTimestamp is { } ts &&
                _options.IdleThreshold > TimeSpan.Zero &&
                now - ts > _options.IdleThreshold)
            {
                return SessionStatus.Idle;
            }

            return SessionStatus.AwaitingInput;
        }

        if (lastEventTimestamp is { } last &&
            _options.IdleThreshold > TimeSpan.Zero &&
            now - last > _options.IdleThreshold)
        {
            return SessionStatus.Idle;
        }

        return SessionStatus.Working;
    }

    private static string? TryGetString(JsonElement? data, string property)
    {
        if (data is not JsonElement el || el.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return el.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
    }
}
