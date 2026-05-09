using CopilotSessionManager.Core.Models;
using Microsoft.Extensions.Logging;

namespace CopilotSessionManager.Core.Sessions;

/// <inheritdoc />
public sealed class SessionLockCleanup : ISessionLockCleanup
{
    private readonly ICopilotPaths _paths;
    private readonly ISessionLockMonitor _monitor;
    private readonly ILogger<SessionLockCleanup> _logger;

    public SessionLockCleanup(
        ICopilotPaths paths,
        ISessionLockMonitor monitor,
        ILogger<SessionLockCleanup> logger)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(monitor);
        ArgumentNullException.ThrowIfNull(logger);

        _paths = paths;
        _monitor = monitor;
        _logger = logger;
    }

    public Task<int> CleanupAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(CleanupSession(sessionId));
    }

    public Task<SessionLockCleanupResult> CleanupAllAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var stateRoot = _paths.SessionStateDirectory;
        if (!Directory.Exists(stateRoot))
        {
            return Task.FromResult(SessionLockCleanupResult.Empty);
        }

        var totalRemoved = 0;
        var sessionsTouched = 0;

        IEnumerable<string> sessionDirs;
        try
        {
            sessionDirs = Directory.EnumerateDirectories(stateRoot);
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Could not enumerate session-state directory {Dir}.", stateRoot);
            return Task.FromResult(SessionLockCleanupResult.Empty);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "Access denied enumerating session-state directory {Dir}.", stateRoot);
            return Task.FromResult(SessionLockCleanupResult.Empty);
        }

        foreach (var dir in sessionDirs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var id = Path.GetFileName(dir);
            if (string.IsNullOrEmpty(id))
            {
                continue;
            }

            var removed = CleanupSession(id);
            if (removed > 0)
            {
                totalRemoved += removed;
                sessionsTouched++;
            }
        }

        return Task.FromResult(new SessionLockCleanupResult(totalRemoved, sessionsTouched));
    }

    private int CleanupSession(string sessionId)
    {
        IReadOnlyList<SessionLockInfo> locks;
        try
        {
            locks = _monitor.GetLocks(sessionId);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not enumerate locks for session {Id}.", sessionId);
            return 0;
        }

        var removed = 0;
        foreach (var info in locks)
        {
            if (info.IsAlive)
            {
                continue;
            }

            try
            {
                File.Delete(info.LockFilePath);
                removed++;
                _logger.LogInformation(
                    "Removed stale lock file for session {Id} (pid {Pid}): {Path}",
                    sessionId, info.ProcessId, info.LockFilePath);
            }
            catch (FileNotFoundException)
            {
                // Already gone — counts as success.
                removed++;
            }
            catch (DirectoryNotFoundException)
            {
                // Session directory disappeared mid-cleanup — nothing to do.
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex,
                    "Failed to delete stale lock file {Path} for session {Id}.",
                    info.LockFilePath, sessionId);
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogWarning(ex,
                    "Access denied deleting stale lock file {Path} for session {Id}.",
                    info.LockFilePath, sessionId);
            }
        }

        return removed;
    }
}
