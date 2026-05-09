using System.Text.RegularExpressions;
using CopilotSessionManager.Core.Sessions;
using Microsoft.Extensions.Logging;

namespace CopilotSessionManager.Core.Onboarding;

/// <summary>
/// Default <see cref="IPrerequisiteChecker"/> implementation. Probes the
/// surrounding system using <see cref="IProcessRunner"/>, the existing
/// <see cref="IPowerShellHostResolver"/>, and <see cref="ICopilotPaths"/>.
/// </summary>
public sealed partial class PrerequisiteChecker : IPrerequisiteChecker
{
    /// <summary>Public so install URLs can also be exposed elsewhere (settings UI etc.).</summary>
    public static class Urls
    {
        public const string PowerShell = "https://github.com/PowerShell/PowerShell/releases";
        public const string CopilotCli = "https://docs.github.com/en/copilot/github-copilot-in-the-cli";
        public const string GhCli = "https://cli.github.com/";
        public const string GhAuth = "https://docs.github.com/en/github-cli/github-cli/quickstart#log-in-to-github";
        public const string CopilotFolder = "https://docs.github.com/en/copilot/github-copilot-in-the-cli";
    }

    [GeneratedRegex(@"PowerShell\s+(\d+)\.(\d+)\.(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex PwshVersionRegex();

    private readonly IProcessRunner _runner;
    private readonly IPowerShellHostResolver _hostResolver;
    private readonly ICopilotPaths _paths;
    private readonly ILogger<PrerequisiteChecker> _logger;

    public PrerequisiteChecker(
        IProcessRunner runner,
        IPowerShellHostResolver hostResolver,
        ICopilotPaths paths,
        ILogger<PrerequisiteChecker> logger)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(hostResolver);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(logger);

        _runner = runner;
        _hostResolver = hostResolver;
        _paths = paths;
        _logger = logger;
    }

    public async Task<IReadOnlyList<PrerequisiteResult>> CheckAllAsync(CancellationToken cancellationToken = default)
    {
        var results = new List<PrerequisiteResult>(capacity: 5);
        results.Add(await CheckPowerShellAsync(cancellationToken).ConfigureAwait(false));
        results.Add(await CheckCopilotCliAsync(cancellationToken).ConfigureAwait(false));
        results.Add(await CheckGhCliAsync(cancellationToken).ConfigureAwait(false));
        results.Add(await CheckGhAuthAsync(cancellationToken).ConfigureAwait(false));
        results.Add(CheckCopilotFolder());
        return results;
    }

    private async Task<PrerequisiteResult> CheckPowerShellAsync(CancellationToken ct)
    {
        var host = _hostResolver.Resolve();
        if (host is null)
        {
            return new PrerequisiteResult(
                "PowerShell 7+",
                PrerequisiteStatus.Failed,
                "No PowerShell host found on PATH. Install PowerShell 7 to enable session resume.",
                Urls.PowerShell);
        }

        var run = await _runner.RunAsync(new ProcessRunRequest(
            host,
            new[] { "-NoProfile", "-NonInteractive", "-Command", "$PSVersionTable.PSVersion.ToString()" },
            TimeoutSeconds: 8), ct).ConfigureAwait(false);

        if (!run.Success)
        {
            return new PrerequisiteResult(
                "PowerShell 7+",
                PrerequisiteStatus.Warning,
                $"Found {host} but version probe failed (exit {run.ExitCode}). Resume may still work.",
                Urls.PowerShell);
        }

        var trimmed = run.StdOut.Trim();
        if (Version.TryParse(trimmed, out var parsed))
        {
            if (parsed.Major >= 7)
            {
                return new PrerequisiteResult(
                    "PowerShell 7+",
                    PrerequisiteStatus.Ok,
                    $"Found PowerShell {parsed} at {host}.",
                    InstallUrl: null);
            }
            return new PrerequisiteResult(
                "PowerShell 7+",
                PrerequisiteStatus.Warning,
                $"Found PowerShell {parsed}. Resume will fall back to Windows PowerShell; install 7+ for the best experience.",
                Urls.PowerShell);
        }

        return new PrerequisiteResult(
            "PowerShell 7+",
            PrerequisiteStatus.Warning,
            $"Could not parse PowerShell version from output: {Truncate(trimmed, 60)}",
            Urls.PowerShell);
    }

    private async Task<PrerequisiteResult> CheckCopilotCliAsync(CancellationToken ct)
    {
        var run = await _runner.RunAsync(new ProcessRunRequest("copilot", new[] { "--version" }), ct).ConfigureAwait(false);
        if (run == ProcessRunResult.NotFound)
        {
            return new PrerequisiteResult(
                "GitHub Copilot CLI",
                PrerequisiteStatus.Failed,
                "copilot CLI not found on PATH.",
                Urls.CopilotCli);
        }
        if (!run.Success)
        {
            return new PrerequisiteResult(
                "GitHub Copilot CLI",
                PrerequisiteStatus.Failed,
                $"copilot --version exited with {run.ExitCode}.",
                Urls.CopilotCli);
        }

        var version = run.StdOut.Trim();
        if (string.IsNullOrEmpty(version))
        {
            return new PrerequisiteResult(
                "GitHub Copilot CLI",
                PrerequisiteStatus.Warning,
                "copilot CLI returned an empty version string.",
                Urls.CopilotCli);
        }
        return new PrerequisiteResult(
            "GitHub Copilot CLI",
            PrerequisiteStatus.Ok,
            $"Found {Truncate(version, 80)}.",
            InstallUrl: null);
    }

    private async Task<PrerequisiteResult> CheckGhCliAsync(CancellationToken ct)
    {
        var run = await _runner.RunAsync(new ProcessRunRequest("gh", new[] { "--version" }), ct).ConfigureAwait(false);
        if (run == ProcessRunResult.NotFound)
        {
            return new PrerequisiteResult(
                "GitHub CLI (gh)",
                PrerequisiteStatus.Failed,
                "gh CLI not found on PATH. Required for branch and pull-request features.",
                Urls.GhCli);
        }
        if (!run.Success)
        {
            return new PrerequisiteResult(
                "GitHub CLI (gh)",
                PrerequisiteStatus.Failed,
                $"gh --version exited with {run.ExitCode}.",
                Urls.GhCli);
        }
        return new PrerequisiteResult(
            "GitHub CLI (gh)",
            PrerequisiteStatus.Ok,
            $"Found {Truncate(run.StdOut.Trim().Split('\n')[0].Trim(), 80)}.",
            InstallUrl: null);
    }

    private async Task<PrerequisiteResult> CheckGhAuthAsync(CancellationToken ct)
    {
        var run = await _runner.RunAsync(new ProcessRunRequest("gh", new[] { "auth", "status" }), ct).ConfigureAwait(false);
        if (run == ProcessRunResult.NotFound)
        {
            return new PrerequisiteResult(
                "GitHub CLI authenticated",
                PrerequisiteStatus.Failed,
                "gh CLI not installed; cannot check authentication.",
                Urls.GhAuth);
        }
        if (run.Success)
        {
            return new PrerequisiteResult(
                "GitHub CLI authenticated",
                PrerequisiteStatus.Ok,
                "gh auth status reports an authenticated session.",
                InstallUrl: null);
        }
        return new PrerequisiteResult(
            "GitHub CLI authenticated",
            PrerequisiteStatus.Failed,
            "gh is installed but not authenticated. Run 'gh auth login'.",
            Urls.GhAuth);
    }

    private PrerequisiteResult CheckCopilotFolder()
    {
        var dir = _paths.SessionStateDirectory;
        try
        {
            if (!Directory.Exists(dir))
            {
                return new PrerequisiteResult(
                    "Copilot session folder",
                    PrerequisiteStatus.Warning,
                    $"{dir} does not exist yet. Run 'copilot' once in a terminal to create it.",
                    Urls.CopilotFolder);
            }

            var probe = Path.Combine(dir, ".csm-write-probe.tmp");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);

            return new PrerequisiteResult(
                "Copilot session folder",
                PrerequisiteStatus.Ok,
                $"Read/write access verified at {dir}.",
                InstallUrl: null);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            _logger.LogWarning(ex, "Could not write probe to {Dir}.", dir);
            return new PrerequisiteResult(
                "Copilot session folder",
                PrerequisiteStatus.Failed,
                $"No write access to {dir}: {ex.Message}",
                Urls.CopilotFolder);
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
