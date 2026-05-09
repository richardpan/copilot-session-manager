using System.Threading;
using System.Threading.Tasks;
using CopilotSessionManager.Core.GitHub.Issues;

namespace CopilotSessionManager.Core.GitHub.Storage;

/// <summary>
/// App-owned per-session store of <see cref="SessionGitHubLinkOverrides"/>.
/// Persists user-supplied repository / branch / PR / issue-link overrides
/// next to the session's other sidecar files in the Copilot session-state
/// directory so they survive an app restart.
/// </summary>
/// <remarks>
/// Implementations must be tolerant: <see cref="GetAsync"/> never throws for
/// a missing or malformed file; it returns <c>null</c> and logs a warning so
/// the discovery pipeline can fall back to un-overridden links.
/// </remarks>
public interface ISessionGitHubLinksStore
{
    /// <summary>
    /// Returns the persisted overrides for <paramref name="sessionId"/>, or
    /// <c>null</c> if none have been written (or if the on-disk file was
    /// missing / malformed).
    /// </summary>
    Task<SessionGitHubLinkOverrides?> GetAsync(
        string sessionId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists <paramref name="overrides"/> for <paramref name="sessionId"/>,
    /// replacing any previous value. If <paramref name="overrides"/> is
    /// <see cref="SessionGitHubLinkOverrides.Empty"/> (all fields <c>null</c>
    /// / no issue refs) the on-disk file is removed instead.
    /// </summary>
    Task SetAsync(
        string sessionId,
        SessionGitHubLinkOverrides overrides,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes any persisted overrides for <paramref name="sessionId"/>.
    /// Idempotent: a missing file is a no-op.
    /// </summary>
    Task ClearAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds <paramref name="issueRef"/> to the persisted issue-ref list for
    /// <paramref name="sessionId"/>. Deduplicates against the existing list
    /// (case-insensitive, canonical-form comparison) so adding the same ref
    /// twice is a safe no-op. Other override fields are preserved.
    /// </summary>
    Task AddIssueRefAsync(
        string sessionId,
        IssueRef issueRef,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes <paramref name="issueRef"/> from the persisted issue-ref list
    /// for <paramref name="sessionId"/>. No-op when the ref is not present
    /// or no overrides exist. Other override fields are preserved.
    /// </summary>
    Task RemoveIssueRefAsync(
        string sessionId,
        IssueRef issueRef,
        CancellationToken cancellationToken = default);
}
