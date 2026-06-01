namespace CopilotSessionManager.Core.Sessions;

/// <summary>
/// User-controlled lifecycle flag for a session, independent of the technical
/// process state (<see cref="SessionStatus"/>). Lets the user mark a session
/// as <see cref="Active"/> (work item still open — come back to this) or
/// <see cref="Closed"/> (truly wrapped up — no need to revisit). The Copilot
/// CLI process being closed does NOT mean the lifecycle is closed; the user
/// decides that explicitly. Defaults to <see cref="Active"/> for any
/// previously-untouched session.
/// </summary>
public enum SessionLifecycleState
{
    /// <summary>Default. Work item / topic is still relevant; revisit later.</summary>
    Active = 0,

    /// <summary>User has marked this session as truly done.</summary>
    Closed = 1,
}
