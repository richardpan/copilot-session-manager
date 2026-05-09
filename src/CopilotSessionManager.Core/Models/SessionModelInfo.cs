namespace CopilotSessionManager.Core.Models;

/// <summary>
/// Snapshot of model selection + usage for a session, distilled from
/// <c>events.jsonl</c>. <see cref="IsFromShutdown"/> indicates whether
/// <see cref="UsageByModel"/> is the authoritative <c>session.shutdown</c>
/// snapshot or only a heuristic fallback (in which case usage may be empty).
/// </summary>
/// <param name="CurrentModelId">
/// Best-effort identifier of the model the session is using (or used last).
/// May be <see langword="null"/> if no model could be detected.
/// </param>
/// <param name="IsFromShutdown">
/// <see langword="true"/> when <see cref="UsageByModel"/> originates from a
/// <c>session.shutdown</c> event, meaning token totals are authoritative.
/// </param>
/// <param name="UsageByModel">
/// Per-model token usage. Empty when only the current model could be detected
/// (e.g., active sessions that have not yet emitted a shutdown event).
/// </param>
public sealed record SessionModelInfo(
    string? CurrentModelId,
    bool IsFromShutdown,
    IReadOnlyDictionary<string, ModelUsage> UsageByModel)
{
    /// <summary>Empty model info — nothing detected.</summary>
    public static readonly SessionModelInfo Empty =
        new(null, false, new Dictionary<string, ModelUsage>(StringComparer.Ordinal));
}
