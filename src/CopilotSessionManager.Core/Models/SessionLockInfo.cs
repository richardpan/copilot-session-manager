namespace CopilotSessionManager.Core.Models;

/// <summary>
/// One Copilot CLI <c>inuse.{pid}.lock</c> file observed on disk. Multiple
/// lock files may exist for a single session (one per running CLI process).
/// </summary>
/// <param name="LockFilePath">Absolute path to the lock file.</param>
/// <param name="ProcessId">PID parsed from the file name.</param>
/// <param name="IsAlive">True when the PID is still a live process.</param>
public sealed record SessionLockInfo(
    string LockFilePath,
    int ProcessId,
    bool IsAlive);
