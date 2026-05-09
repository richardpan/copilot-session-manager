using CopilotSessionManager.Core.Models;

namespace CopilotSessionManager.Core.Sessions;

/// <summary>
/// Removes stale <c>inuse.{pid}.lock</c> files left behind when a Copilot CLI
/// process crashes. Only files whose PID is no longer alive are deleted.
/// Never touches transcripts, events, or session metadata.
/// </summary>
public interface ISessionLockCleanup
{
    /// <summary>
    /// Deletes only the stale (PID-not-alive) lock files belonging to
    /// <paramref name="sessionId"/>. Live locks are left untouched.
    /// </summary>
    /// <returns>The number of lock files actually deleted.</returns>
    Task<int> CleanupAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes every stale lock file across all known sessions. Live locks
    /// are left untouched.
    /// </summary>
    /// <returns>
    /// A summary record listing the total number of stale locks removed and
    /// the number of sessions affected.
    /// </returns>
    Task<SessionLockCleanupResult> CleanupAllAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Aggregate result of a bulk cleanup pass.
/// </summary>
public sealed record SessionLockCleanupResult(int LocksRemoved, int SessionsAffected)
{
    public static SessionLockCleanupResult Empty { get; } = new(0, 0);
}
