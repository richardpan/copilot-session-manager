using System.Threading;
using System.Threading.Tasks;

namespace CopilotSessionManager.Core.Sessions;

/// <summary>
/// Outcome of an attempt to hard-delete a session (#106).
/// </summary>
/// <param name="Success">True when the session folder was removed.</param>
/// <param name="FolderPath">Resolved on-disk path that was targeted.</param>
/// <param name="ErrorMessage">User-facing error when <see cref="Success"/> is false.</param>
public sealed record SessionDeletionResult(bool Success, string FolderPath, string? ErrorMessage)
{
    public static SessionDeletionResult Ok(string folderPath) => new(true, folderPath, null);

    public static SessionDeletionResult Failed(string folderPath, string errorMessage) =>
        new(false, folderPath, errorMessage);
}

/// <summary>
/// Hard-deletes a Copilot session (#106). Removes the on-disk
/// <c>~/.copilot/session-state/&lt;id&gt;/</c> directory recursively and
/// clears any CSM-side overrides for the session.
/// </summary>
/// <remarks>
/// This is a destructive, user-initiated write into Copilot CLI's storage
/// area. It is the one documented exception to ADR-002's read-only stance —
/// see the PR that introduced this service. The Copilot CLI's own
/// <c>session-store.db</c> is left untouched; the CLI repairs its index when
/// the per-session folder disappears.
/// </remarks>
public interface ISessionDeletionService
{
    /// <summary>
    /// Deletes the session identified by <paramref name="sessionId"/>.
    /// Returns a <see cref="SessionDeletionResult"/> describing whether the
    /// folder was actually removed. Never throws for "missing", "locked",
    /// or "permission denied" — these flow through as
    /// <see cref="SessionDeletionResult.Failed"/>.
    /// </summary>
    Task<SessionDeletionResult> DeleteAsync(
        string sessionId,
        CancellationToken cancellationToken = default);
}
