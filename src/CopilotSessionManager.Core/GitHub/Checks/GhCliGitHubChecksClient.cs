using System;
using System.Threading;
using System.Threading.Tasks;
using CopilotSessionManager.Core.Onboarding;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CopilotSessionManager.Core.GitHub.Checks;

/// <summary>
/// <see cref="IGitHubChecksClient"/> that shells out to <c>gh pr checks</c>.
/// Mirrors <see cref="GhCliGitHubClient"/>: every "expected" failure
/// (<c>gh</c> missing, no checks, non-zero exit, parse failure) returns
/// <c>null</c> so callers don't need try/catch.
/// </summary>
/// <remarks>
/// Every invocation reports an outcome to the optional
/// <see cref="IGitHubAvailabilityProvider"/> (network errors → Offline,
/// auth errors → Unauthenticated, success → Available) so the global
/// availability state stays current alongside the existing PR-list probe.
/// </remarks>
public sealed class GhCliGitHubChecksClient : IGitHubChecksClient
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

    private readonly ILogger<GhCliGitHubChecksClient> _logger;
    private readonly IProcessRunner _runner;
    private readonly IGitHubAvailabilityProvider? _availability;
    private readonly string _ghExecutable;
    private readonly TimeSpan _timeout;

    public GhCliGitHubChecksClient(ILogger<GhCliGitHubChecksClient> logger)
        : this(
            logger,
            new ProcessRunner(NullLogger<ProcessRunner>.Instance),
            availability: null,
            ghExecutable: "gh",
            timeout: DefaultTimeout)
    {
    }

    /// <summary>
    /// DI-preferred constructor — picked when both <see cref="IProcessRunner"/>
    /// and <see cref="IGitHubAvailabilityProvider"/> are registered.
    /// </summary>
    public GhCliGitHubChecksClient(
        ILogger<GhCliGitHubChecksClient> logger,
        IProcessRunner runner,
        IGitHubAvailabilityProvider availability)
        : this(logger, runner, availability, ghExecutable: "gh", timeout: DefaultTimeout)
    {
    }

    public GhCliGitHubChecksClient(
        ILogger<GhCliGitHubChecksClient> logger,
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

    public async Task<PullRequestCheckSummary?> GetChecksAsync(
        string repositorySlug,
        int pullRequestNumber,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(repositorySlug) || pullRequestNumber <= 0)
        {
            return null;
        }

        var args = new[]
        {
            "pr",
            "checks",
            pullRequestNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--repo",
            repositorySlug,
            "--json",
            "name,state,bucket",
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
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(
                ex,
                "Unexpected error running gh pr checks for {Repo}#{Pr}.",
                repositorySlug,
                pullRequestNumber);
            return null;
        }

        ReportAvailability(result, repositorySlug, pullRequestNumber);

        // gh pr checks exits non-zero when there are failing checks (exit 1)
        // or when checks are still pending (exit 8). Both still produce
        // valid JSON on stdout, so attempt to parse before bailing out.
        var parsed = GhChecksJsonParser.Parse(result.StdOut);
        if (parsed is not null)
        {
            return parsed;
        }

        if (result.ExitCode != 0)
        {
            _logger.LogDebug(
                "gh pr checks exited {Exit} for {Repo}#{Pr}: {Stderr}",
                result.ExitCode,
                repositorySlug,
                pullRequestNumber,
                result.StdErr.Trim());
        }

        return null;
    }

    private void ReportAvailability(ProcessRunResult result, string repositorySlug, int pullRequestNumber)
    {
        if (_availability is null)
        {
            return;
        }

        var (state, message) = GhCliResultClassifier.Classify(result);

        // gh pr checks exits non-zero on failing/pending checks (exit 1, 8)
        // even when gh itself is healthy. Don't report those as "Offline".
        // Only push state changes when the classifier was confident.
        if (state == GitHubAvailability.Available && result.ExitCode != 0)
        {
            return;
        }

        if (state != GitHubAvailability.Available)
        {
            _logger.LogDebug(
                "gh availability classified as {State} for checks {Repo}#{Pr}: {Message}",
                state,
                repositorySlug,
                pullRequestNumber,
                message);
        }

        _availability.Report(state, message);
    }
}
