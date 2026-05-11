using Microsoft.Extensions.Logging;

namespace CopilotSessionManager.Core.Sessions;

/// <summary>
/// Locates a PowerShell host on the machine. Defaults to PowerShell 7
/// (<c>pwsh</c>) and falls back to Windows PowerShell (<c>powershell</c>).
/// </summary>
public interface IPowerShellHostResolver
{
    /// <summary>
    /// Returns the absolute path to the PowerShell executable to spawn, or
    /// <c>null</c> when nothing was found on PATH.
    /// </summary>
    string? Resolve();
}

/// <summary>
/// Default <see cref="IPowerShellHostResolver"/> that walks <c>PATH</c> looking
/// for <c>pwsh.exe</c> first, then <c>powershell.exe</c>.
/// </summary>
public sealed class PathPowerShellHostResolver : IPowerShellHostResolver
{
    private static readonly string[] Candidates = OperatingSystem.IsWindows()
        ? new[] { "pwsh.exe", "powershell.exe" }
        : new[] { "pwsh" };

    public string? Resolve()
    {
        foreach (var candidate in Candidates)
        {
            var hit = FindOnPath(candidate);
            if (hit is not null)
            {
                return hit;
            }
        }
        return null;
    }

    private static string? FindOnPath(string fileName)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathEnv))
        {
            return null;
        }

        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate;
            try
            {
                candidate = Path.Combine(dir.Trim(), fileName);
            }
            catch (ArgumentException)
            {
                continue;
            }

            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        return null;
    }
}

/// <inheritdoc />
public sealed class PowerShellSessionLauncher : ISessionLauncher
{
    private readonly IProcessLauncher _processLauncher;
    private readonly IPowerShellHostResolver _hostResolver;
    private readonly ILogger<PowerShellSessionLauncher> _logger;

    public PowerShellSessionLauncher(
        IProcessLauncher processLauncher,
        IPowerShellHostResolver hostResolver,
        ILogger<PowerShellSessionLauncher> logger)
    {
        ArgumentNullException.ThrowIfNull(processLauncher);
        ArgumentNullException.ThrowIfNull(hostResolver);
        ArgumentNullException.ThrowIfNull(logger);

        _processLauncher = processLauncher;
        _hostResolver = hostResolver;
        _logger = logger;
    }

    public Task<SessionLaunchResult> LaunchAsync(
        string sessionId,
        string? workingDirectory = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        cancellationToken.ThrowIfCancellationRequested();

        var pwsh = _hostResolver.Resolve()
            ?? throw new InvalidOperationException("Could not locate pwsh.exe or powershell.exe on PATH.");

        var cwd = ResolveWorkingDirectory(workingDirectory);

        // Quote the session id defensively even though IDs are normally safe.
        var safeId = sessionId.Replace("'", "''", StringComparison.Ordinal);
        var command = $"copilot --resume '{safeId}'";

        return RunAsync(pwsh, command, cwd, sessionId);
    }

    public Task<SessionLaunchResult> LaunchNewAsync(
        string? workingDirectory = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var pwsh = _hostResolver.Resolve()
            ?? throw new InvalidOperationException("Could not locate pwsh.exe or powershell.exe on PATH.");

        var cwd = ResolveWorkingDirectory(workingDirectory);
        const string command = "copilot";

        return RunAsync(pwsh, command, cwd, sessionId: null);
    }

    private Task<SessionLaunchResult> RunAsync(string pwsh, string command, string cwd, string? sessionId)
    {
        var args = new[]
        {
            "-NoExit",
            "-Command",
            command,
        };

        var request = new ProcessStartRequest(
            FileName: pwsh,
            Arguments: args,
            WorkingDirectory: cwd,
            UseShellExecute: true);

        try
        {
            var pid = _processLauncher.Start(request);
            if (sessionId is null)
            {
                _logger.LogInformation(
                    "Launched fresh PowerShell Copilot session pid={Pid} cwd={Cwd}",
                    pid, cwd);
            }
            else
            {
                _logger.LogInformation(
                    "Launched PowerShell session {SessionId} pid={Pid} cwd={Cwd}",
                    sessionId, pid, cwd);
            }
            return Task.FromResult(new SessionLaunchResult(pid, pwsh, command, cwd));
        }
        catch (Exception ex)
        {
            if (sessionId is null)
            {
                _logger.LogError(ex, "Failed to launch fresh PowerShell Copilot session.");
            }
            else
            {
                _logger.LogError(ex, "Failed to launch PowerShell for session {SessionId}.", sessionId);
            }
            throw;
        }
    }

    private static string ResolveWorkingDirectory(string? requested)
    {
        if (!string.IsNullOrWhiteSpace(requested) && Directory.Exists(requested))
        {
            return requested;
        }
        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }
}
