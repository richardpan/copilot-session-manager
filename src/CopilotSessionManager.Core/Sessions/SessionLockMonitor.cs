using System.Globalization;
using System.Text.RegularExpressions;
using CopilotSessionManager.Core.Models;
using Microsoft.Extensions.Logging;

namespace CopilotSessionManager.Core.Sessions;

/// <inheritdoc />
public sealed partial class SessionLockMonitor : ISessionLockMonitor
{
    private readonly ICopilotPaths _paths;
    private readonly IProcessChecker _processChecker;
    private readonly ILogger<SessionLockMonitor> _logger;

    public SessionLockMonitor(
        ICopilotPaths paths,
        IProcessChecker processChecker,
        ILogger<SessionLockMonitor> logger)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(processChecker);
        ArgumentNullException.ThrowIfNull(logger);

        _paths = paths;
        _processChecker = processChecker;
        _logger = logger;
    }

    public IReadOnlyList<SessionLockInfo> GetLocks(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        var sessionDir = Path.Combine(_paths.SessionStateDirectory, sessionId);
        if (!Directory.Exists(sessionDir))
        {
            return Array.Empty<SessionLockInfo>();
        }

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(sessionDir, "inuse.*.lock", SearchOption.TopDirectoryOnly);
        }
        catch (IOException ex)
        {
            _logger.LogDebug(ex, "Could not enumerate lock files in {Dir}.", sessionDir);
            return Array.Empty<SessionLockInfo>();
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogDebug(ex, "Access denied enumerating lock files in {Dir}.", sessionDir);
            return Array.Empty<SessionLockInfo>();
        }

        var results = new List<SessionLockInfo>();
        foreach (var path in files)
        {
            var pid = TryParsePid(Path.GetFileName(path));
            if (pid is null)
            {
                _logger.LogDebug("Skipping lock file with unparseable PID: {Path}", path);
                continue;
            }

            var isAlive = _processChecker.IsAlive(pid.Value);
            results.Add(new SessionLockInfo(path, pid.Value, isAlive));
        }

        return results;
    }

    private static int? TryParsePid(string fileName)
    {
        var match = LockFileNameRegex().Match(fileName);
        if (!match.Success)
        {
            return null;
        }

        return int.TryParse(match.Groups[1].ValueSpan, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid)
            ? pid
            : null;
    }

    [GeneratedRegex(@"^inuse\.(\d+)\.lock$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LockFileNameRegex();
}
