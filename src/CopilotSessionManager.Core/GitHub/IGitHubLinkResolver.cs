using CopilotSessionManager.Core.Models;

namespace CopilotSessionManager.Core.GitHub;

/// <summary>
/// Pure, deterministic builder of GitHub click-through URLs from a
/// <see cref="Session"/>'s repository slug and branch name. Performs no I/O.
/// </summary>
public interface IGitHubLinkResolver
{
    /// <summary>
    /// Resolves repository + branch URLs for <paramref name="session"/>.
    /// The returned links never include a <see cref="PullRequestInfo"/>;
    /// PR data is fetched separately by <see cref="IGitHubClient"/>.
    /// </summary>
    SessionGitHubLinks Resolve(Session session);
}
