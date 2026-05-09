using CopilotSessionManager.Core.Models;

namespace CopilotSessionManager.Core.Sessions;

/// <summary>
/// Computes a <see cref="SessionStatus"/> by combining lock file presence with
/// the tail of a session's <c>events.jsonl</c> stream.
/// </summary>
public interface ISessionStatusEvaluator
{
    /// <summary>
    /// Evaluates the current status for one session.
    /// </summary>
    /// <param name="sessionId">The session id (folder name under <c>session-state/</c>).</param>
    /// <param name="locks">Lock files observed for the session (may be empty).</param>
    /// <param name="copilotVersion">CLI version that produced the events file.</param>
    /// <param name="now">"Now" used for the idle threshold (UTC).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<SessionStatus> EvaluateAsync(
        string sessionId,
        IReadOnlyList<SessionLockInfo> locks,
        CopilotVersion copilotVersion,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);
}
