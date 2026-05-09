using CopilotSessionManager.Core.Models;

namespace CopilotSessionManager.Core.Sessions;

/// <summary>
/// Discovers <c>inuse.{pid}.lock</c> files for a Copilot session and reports
/// whether each PID is alive or orphaned.
/// </summary>
public interface ISessionLockMonitor
{
    /// <summary>
    /// Lists every lock file under <c>session-state/{sessionId}/</c>. Returns
    /// an empty list if the directory does not exist or contains no locks.
    /// </summary>
    IReadOnlyList<SessionLockInfo> GetLocks(string sessionId);
}
