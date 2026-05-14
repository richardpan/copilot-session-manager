using System.Collections.Generic;
using CopilotSessionManager.Core.Models;

namespace CopilotSessionManager.Core.Sessions;

/// <summary>
/// Inputs the README renderer needs to produce its auto-generated sections.
/// Pure data — no IO. Constructed by the readme service.
/// </summary>
public sealed record SessionReadmeContext(
    Session Session,
    SessionType Label,
    IReadOnlyList<SessionCheckpointSummary> Checkpoints,
    SessionEventSummary EventSummary,
    IReadOnlyList<SubagentSummary> Subagents)
{
    /// <summary>
    /// Backward-compatible constructor used by older call sites that
    /// pre-date the V1.3 events-derived sections. Defaults
    /// <see cref="EventSummary"/> to <see cref="SessionEventSummary.Empty"/>
    /// and <see cref="Subagents"/> to an empty list, which yields the same
    /// renderer output as before V1.3 plus skipped/empty placeholders for
    /// the new sections.
    /// </summary>
    public SessionReadmeContext(
        Session session,
        SessionType label,
        IReadOnlyList<SessionCheckpointSummary> checkpoints)
        : this(session, label, checkpoints, SessionEventSummary.Empty, System.Array.Empty<SubagentSummary>())
    {
    }
}

