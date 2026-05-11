namespace CopilotSessionManager.Core.Sessions;

/// <summary>
/// Result of attempting to launch a session.
/// </summary>
/// <param name="ProcessId">PID of the spawned process, or null when the
/// launcher returned without producing a child process.</param>
/// <param name="Executable">Resolved executable path actually used.</param>
/// <param name="Arguments">Argument string passed to the process.</param>
/// <param name="WorkingDirectory">Working directory of the spawned process.</param>
public sealed record SessionLaunchResult(int? ProcessId, string Executable, string Arguments, string WorkingDirectory);

/// <summary>
/// Launches an external terminal hosting <c>copilot --resume &lt;id&gt;</c> for a
/// given session. Distinct from the V2 in-app embedded ConPTY terminal (#30).
/// </summary>
public interface ISessionLauncher
{
    /// <summary>
    /// Spawns an external PowerShell window that resumes the Copilot CLI
    /// session identified by <paramref name="sessionId"/>. Returns a result
    /// describing what was launched. Throws when no PowerShell host could be
    /// found on the system.
    /// </summary>
    /// <param name="sessionId">Copilot session id (e.g. a UUID).</param>
    /// <param name="workingDirectory">
    /// Optional working directory for the new PowerShell window. Defaults to
    /// the user's profile when null/empty/missing on disk.
    /// </param>
    Task<SessionLaunchResult> LaunchAsync(
        string sessionId,
        string? workingDirectory = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Spawns an external PowerShell window running a fresh <c>copilot</c>
    /// invocation (no <c>--resume</c>) so the CLI generates a brand-new
    /// session. The new session id is not known until the discovery service
    /// next scans <c>~/.copilot/session-state</c>; callers should refresh
    /// shortly after the launch returns.
    /// </summary>
    /// <param name="workingDirectory">
    /// Optional working directory for the new PowerShell window. Defaults to
    /// the user's profile when null/empty/missing on disk.
    /// </param>
    Task<SessionLaunchResult> LaunchNewAsync(
        string? workingDirectory = null,
        CancellationToken cancellationToken = default);
}
