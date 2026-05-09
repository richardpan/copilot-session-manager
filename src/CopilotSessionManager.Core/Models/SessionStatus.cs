namespace CopilotSessionManager.Core.Models;

/// <summary>
/// High-level session state derived from events + lock files.
/// </summary>
public enum SessionStatus
{
    /// <summary>State could not be determined yet.</summary>
    Unknown = 0,

    /// <summary>No active lock file: the session is not running.</summary>
    Inactive = 1,

    /// <summary>Lock file present, last event is <c>assistant.turn_start</c> with no matching end.</summary>
    Working = 2,

    /// <summary>Lock file present, a <c>permission.requested</c> is open.</summary>
    AwaitingApproval = 3,

    /// <summary>Lock file present, last event is <c>assistant.turn_end</c>.</summary>
    AwaitingInput = 4,

    /// <summary>Lock file present, but no event in a long time.</summary>
    Idle = 5,

    /// <summary>A lock file points at a PID that is no longer alive.</summary>
    Orphaned = 6,
}
