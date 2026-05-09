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
    IReadOnlyList<SessionCheckpointSummary> Checkpoints);
