namespace CopilotSessionManager.Core.Sessions;

/// <summary>
/// Tiny abstraction over <c>System.Diagnostics.Process</c> so lock monitoring
/// can be unit-tested without depending on real running processes.
/// </summary>
public interface IProcessChecker
{
    /// <summary>
    /// Returns <c>true</c> if a process with the given PID is currently
    /// alive on the local machine. Returns <c>false</c> for unknown / dead
    /// PIDs and for PIDs &lt;= 0.
    /// </summary>
    bool IsAlive(int pid);
}
