using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CopilotSessionManager.Core.GitHub.Issues;

/// <summary>
/// Surfaces issue references parsed out of a session's auto-generated
/// <c>SESSION-README.md</c> (#71). Implementations are read-only and pure
/// downstream of the README — they never mutate persisted state.
/// </summary>
public interface IReadmeIssueRefProvider
{
    /// <summary>
    /// Returns the <see cref="IssueRef"/>s currently mentioned in the
    /// session's README, deduplicated and capped at
    /// <see cref="IssueRefScanner.MaxRefs"/>.
    /// </summary>
    /// <param name="sessionId">The session whose README to scan.</param>
    /// <param name="defaultOwnerRepo">
    /// Owner/repo to resolve bare <c>#NN</c> refs against. May be
    /// <c>null</c>; bare refs are skipped in that case.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An empty list when the README is missing, blank, or unreadable.</returns>
    Task<IReadOnlyList<IssueRef>> GetParsedRefsAsync(
        string sessionId,
        string? defaultOwnerRepo,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Raised after the README for a session has been rewritten on disk so
    /// listeners can re-scan. May be invoked from a background thread.
    /// </summary>
    event EventHandler<ReadmeIssueRefsChangedEventArgs>? ReadmeChanged;
}

/// <summary>Payload for <see cref="IReadmeIssueRefProvider.ReadmeChanged"/>.</summary>
public sealed class ReadmeIssueRefsChangedEventArgs : EventArgs
{
    public ReadmeIssueRefsChangedEventArgs(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        SessionId = sessionId;
    }

    public string SessionId { get; }
}
