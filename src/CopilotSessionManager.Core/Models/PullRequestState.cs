namespace CopilotSessionManager.Core.Models;

/// <summary>
/// Coarse state of a pull request as surfaced on a session card.
/// </summary>
public enum PullRequestState
{
    Unknown = 0,
    Open,
    Draft,
    Merged,
    Closed,
}
