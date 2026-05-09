using System.Threading;
using System.Threading.Tasks;

namespace CopilotSessionManager.Core.GitHub.Checks;

/// <summary>
/// Network-facing lookup for the CI check rollup of a pull request.
/// Implementations must never throw for the common cases (no checks,
/// missing tooling, transient failure) — they return <c>null</c> instead
/// so the UI can simply skip the indicator.
/// </summary>
public interface IGitHubChecksClient
{
    /// <summary>
    /// Returns the rollup of CI checks on the head commit of
    /// <paramref name="pullRequestNumber"/> in <paramref name="repositorySlug"/>,
    /// or <c>null</c> when the lookup cannot be performed.
    /// </summary>
    Task<PullRequestCheckSummary?> GetChecksAsync(
        string repositorySlug,
        int pullRequestNumber,
        CancellationToken cancellationToken = default);
}
