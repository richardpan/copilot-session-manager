namespace CopilotSessionManager.Core.Models;

/// <summary>
/// Click-through links surfaced for a session — repository home, the
/// session's working branch, and the auto-detected pull request (if any).
/// All fields are nullable; the resolver returns <c>null</c> URLs when there
/// isn't enough information to construct one, and <see cref="PullRequest"/> is
/// only populated after an out-of-band lookup.
/// </summary>
public sealed record SessionGitHubLinks(
    string? RepositoryUrl,
    string? BranchUrl,
    PullRequestInfo? PullRequest)
{
    public static readonly SessionGitHubLinks Empty = new(null, null, null);

    /// <summary>True when at least one click-through is available.</summary>
    public bool HasAnyLink =>
        RepositoryUrl is not null || BranchUrl is not null || PullRequest is not null;
}
