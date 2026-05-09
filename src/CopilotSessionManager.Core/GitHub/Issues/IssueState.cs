namespace CopilotSessionManager.Core.GitHub.Issues;

/// <summary>
/// Coarse-grained state classification for a GitHub issue. Mirrors the
/// values returned by <c>gh issue view --json state</c>.
/// </summary>
public enum IssueState
{
    /// <summary>State could not be determined (e.g. lookup hasn't run yet).</summary>
    Unknown,

    /// <summary>The issue is currently open.</summary>
    Open,

    /// <summary>The issue has been closed (any reason).</summary>
    Closed,
}
