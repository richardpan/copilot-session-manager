using CopilotSessionManager.Core.Models;

namespace CopilotSessionManager.Core.GitHub;

/// <summary>
/// Network-facing lookup for the pull request associated with a
/// <c>(repositorySlug, headBranch)</c> pair. Implementations must never
/// throw for the common cases (no PR, missing tooling, transient failure)
/// — they return <c>null</c> instead so the UI can simply skip the badge.
/// </summary>
public interface IGitHubClient
{
    /// <summary>
    /// Returns the most recent pull request whose head matches
    /// <paramref name="headBranch"/> in <paramref name="repositorySlug"/>.
    /// Returns <c>null</c> if no PR exists or the lookup cannot be performed.
    /// </summary>
    Task<PullRequestInfo?> FindPullRequestAsync(
        string repositorySlug,
        string headBranch,
        CancellationToken cancellationToken = default);
}
