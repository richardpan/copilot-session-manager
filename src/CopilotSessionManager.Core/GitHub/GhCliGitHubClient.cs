using CopilotSessionManager.Core.Models;
using CopilotSessionManager.Core.Onboarding;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CopilotSessionManager.Core.GitHub;

/// <summary>
/// <see cref="IGitHubClient"/> that shells out to the <c>gh</c> CLI. Falls
/// back to <c>null</c> for every "expected" failure (gh missing, no PR,
/// non-zero exit, parse failure) so callers don't need try/catch.
/// </summary>
/// <remarks>
/// Every invocation reports an outcome to the optional
/// <see cref="IGitHubAvailabilityProvider"/>: success → Available
/// (auto-recovery), network errors → Offline, auth errors →
/// Unauthenticated. State changes are debounced inside the provider, so
/// repeated identical failures don't spam subscribers.
/// </remarks>
public sealed class GhCliGitHubClient : IGitHubClient
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

    private readonly ILogger<GhCliGitHubClient> _logger;
    private readonly IProcessRunner _runner;
    private readonly IGitHubAvailabilityProvider? _availability;
    private readonly string _ghExecutable;
    private readonly TimeSpan _timeout;

    public GhCliGitHubClient(ILogger<GhCliGitHubClient> logger)
        : this(
            logger,
            new ProcessRunner(NullLogger<ProcessRunner>.Instance),
            availability: null,
            ghExecutable: "gh",
            timeout: DefaultTimeout)
    {
    }

    public GhCliGitHubClient(ILogger<GhCliGitHubClient> logger, string ghExecutable, TimeSpan timeout)
        : this(
            logger,
            new ProcessRunner(NullLogger<ProcessRunner>.Instance),
            availability: null,
            ghExecutable: ghExecutable,
            timeout: timeout)
    {
    }

    /// <summary>
    /// DI-preferred constructor: when both <see cref="IProcessRunner"/> and
    /// <see cref="IGitHubAvailabilityProvider"/> are registered, this
    /// overload is selected by Microsoft.Extensions.DependencyInjection (it
    /// has the most resolvable parameters).
    /// </summary>
    public GhCliGitHubClient(
        ILogger<GhCliGitHubClient> logger,
        IProcessRunner runner,
        IGitHubAvailabilityProvider availability)
        : this(logger, runner, availability, ghExecutable: "gh", timeout: DefaultTimeout)
    {
    }

    public GhCliGitHubClient(
        ILogger<GhCliGitHubClient> logger,
        IProcessRunner runner,
        IGitHubAvailabilityProvider? availability,
        string ghExecutable,
        TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentException.ThrowIfNullOrWhiteSpace(ghExecutable);

        _logger = logger;
        _runner = runner;
        _availability = availability;
        _ghExecutable = ghExecutable;
        _timeout = timeout > TimeSpan.Zero ? timeout : DefaultTimeout;
    }

    public async Task<PullRequestInfo?> FindPullRequestAsync(
        string repositorySlug,
        string headBranch,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(repositorySlug) || string.IsNullOrWhiteSpace(headBranch))
        {
            return null;
        }

        var args = new[]
        {
            "pr",
            "list",
            "--repo",
            repositorySlug,
            "--head",
            headBranch,
            "--state",
            "all",
            "--limit",
            "1",
            "--json",
            "number,title,state,isDraft,url",
        };

        ProcessRunResult result;
        try
        {
            result = await _runner
                .RunAsync(
                    new ProcessRunRequest(_ghExecutable, args, TimeoutSeconds: (int)_timeout.TotalSeconds),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Caller-driven cancel — don't treat as a network failure.
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(
                ex,
                "Unexpected error running gh pr list for {Repo}#{Branch}.",
                repositorySlug,
                headBranch);
            // Don't change availability — unknown class of failure.
            return null;
        }

        // Always classify, regardless of outcome, so the provider tracks
        // current state in real time.
        ReportAvailability(result, repositorySlug, headBranch);

        if (result.ExitCode != 0)
        {
            _logger.LogDebug(
                "gh pr list exited {Exit} for {Repo}#{Branch}: {Stderr}",
                result.ExitCode,
                repositorySlug,
                headBranch,
                result.StdErr.Trim());
            return null;
        }

        try
        {
            return GhPullRequestJsonParser.ParseFirst(result.StdOut);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(
                ex,
                "Failed to parse gh pr list output for {Repo}#{Branch}.",
                repositorySlug,
                headBranch);
            return null;
        }
    }

    private void ReportAvailability(ProcessRunResult result, string repositorySlug, string headBranch)
    {
        if (_availability is null)
        {
            return;
        }

        var (state, message) = GhCliResultClassifier.Classify(result);

        // Skip "unknown" failures — Classify returns Available + null when it
        // can't tell. We only want to overwrite Current with Available on
        // genuine successes (exit code 0).
        if (state == GitHubAvailability.Available && result.ExitCode != 0)
        {
            return;
        }

        if (state != GitHubAvailability.Available)
        {
            _logger.LogDebug(
                "gh availability classified as {State} for {Repo}#{Branch}: {Message}",
                state,
                repositorySlug,
                headBranch,
                message);
        }

        _availability.Report(state, message);
    }
}
