using System.Threading;
using System.Threading.Tasks;

namespace CopilotSessionManager.Core.GitHub.Issues;

/// <summary>
/// Network-facing lookup for a single GitHub issue. Implementations must
/// never throw for the common cases (issue not found, missing tooling,
/// transient failure) — they return <c>null</c> instead so the UI can
/// simply show a placeholder badge.
/// </summary>
public interface IGitHubIssuesClient
{
    /// <summary>
    /// Returns metadata for <paramref name="issueRef"/>, or <c>null</c>
    /// when the issue cannot be resolved (missing, deleted, gh missing,
    /// transient network/auth failure).
    /// </summary>
    Task<IssueInfo?> GetIssueAsync(IssueRef issueRef, CancellationToken cancellationToken = default);
}
