using System;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CopilotSessionManager.Core.Onboarding;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CopilotSessionManager.Core.GitHub.Issues;

/// <summary>
/// <see cref="IGitHubIssuesClient"/> that shells out to <c>gh issue view</c>.
/// Mirrors <see cref="Checks.GhCliGitHubChecksClient"/>: every "expected"
/// failure (issue missing, <c>gh</c> missing, non-zero exit, parse failure)
/// returns <c>null</c> so callers don't need try/catch.
/// </summary>
/// <remarks>
/// Reports outcomes to the optional <see cref="IGitHubAvailabilityProvider"/>
/// (network errors → Offline, auth errors → Unauthenticated, success →
/// Available) so the global availability state stays current alongside the
/// existing PR-list and PR-checks probes. A <c>404 not found</c> for a
/// specific issue is treated as a benign "no such issue" and does NOT flip
/// availability to Offline.
/// </remarks>
public sealed class GhCliGitHubIssuesClient : IGitHubIssuesClient
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

    private readonly ILogger<GhCliGitHubIssuesClient> _logger;
    private readonly IProcessRunner _runner;
    private readonly IGitHubAvailabilityProvider? _availability;
    private readonly string _ghExecutable;
    private readonly TimeSpan _timeout;

    public GhCliGitHubIssuesClient(ILogger<GhCliGitHubIssuesClient> logger)
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
    public GhCliGitHubIssuesClient(
        ILogger<GhCliGitHubIssuesClient> logger,
        IProcessRunner runner,
        IGitHubAvailabilityProvider availability)
        : this(logger, runner, availability, ghExecutable: "gh", timeout: DefaultTimeout)
    {
    }

    public GhCliGitHubIssuesClient(
        ILogger<GhCliGitHubIssuesClient> logger,
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

    public async Task<IssueInfo?> GetIssueAsync(IssueRef issueRef, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(issueRef);

        var args = new[]
        {
            "issue",
            "view",
            issueRef.Number.ToString(CultureInfo.InvariantCulture),
            "--repo",
            issueRef.OwnerRepo,
            "--json",
            "number,title,state,url",
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
                "Unexpected error running gh issue view for {Repo}#{Issue}.",
                issueRef.OwnerRepo,
                issueRef.Number);
            return null;
        }

        // 404 "no issue" is a normal outcome (user typed a stale number) —
        // don't flip availability to Offline for it.
        if (IsNotFound(result))
        {
            _logger.LogDebug(
                "gh issue view reported issue {Repo}#{Issue} not found.",
                issueRef.OwnerRepo,
                issueRef.Number);
            return null;
        }

        ReportAvailability(result, issueRef);

        if (result.ExitCode != 0)
        {
            _logger.LogDebug(
                "gh issue view exited {Exit} for {Repo}#{Issue}: {Stderr}",
                result.ExitCode,
                issueRef.OwnerRepo,
                issueRef.Number,
                result.StdErr.Trim());
            return null;
        }

        return ParseIssue(result.StdOut, issueRef);
    }

    private static bool IsNotFound(ProcessRunResult result)
    {
        if (result.ExitCode == 0)
        {
            return false;
        }
        var combined = (result.StdErr + "\n" + result.StdOut).ToLowerInvariant();
        return combined.Contains("could not resolve to an issuable", StringComparison.Ordinal)
            || combined.Contains("no issues found", StringComparison.Ordinal)
            || combined.Contains("404 not found", StringComparison.Ordinal)
            || (combined.Contains("not found", StringComparison.Ordinal)
                && combined.Contains("issue", StringComparison.Ordinal));
    }

    private void ReportAvailability(ProcessRunResult result, IssueRef issueRef)
    {
        if (_availability is null)
        {
            return;
        }

        var (state, message) = GhCliResultClassifier.Classify(result);

        // Don't push "Available" when we got a non-zero exit but the
        // classifier shrugged — could be a transient error we can't
        // categorise. Only commit confident transitions.
        if (state == GitHubAvailability.Available && result.ExitCode != 0)
        {
            return;
        }

        if (state != GitHubAvailability.Available)
        {
            _logger.LogDebug(
                "gh availability classified as {State} for issue {Repo}#{Issue}: {Message}",
                state,
                issueRef.OwnerRepo,
                issueRef.Number,
                message);
        }

        _availability.Report(state, message);
    }

    private IssueInfo? ParseIssue(string json, IssueRef issueRef)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var title = TryGetString(doc.RootElement, "title") ?? string.Empty;
            var stateText = TryGetString(doc.RootElement, "state");
            var url = TryGetString(doc.RootElement, "url");

            var state = stateText?.ToUpperInvariant() switch
            {
                "OPEN" => IssueState.Open,
                "CLOSED" => IssueState.Closed,
                _ => IssueState.Unknown,
            };

            // Prefer the canonical URL we can compute from the ref so the
            // value is stable across gh releases. If gh ever stops returning
            // a URL we still have one.
            var canonicalUrl = !string.IsNullOrWhiteSpace(url) ? url! : issueRef.ToCanonicalUrl();

            return new IssueInfo(issueRef, title, state, canonicalUrl);
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(
                ex,
                "Failed to parse gh issue view payload for {Repo}#{Issue}.",
                issueRef.OwnerRepo,
                issueRef.Number);
            return null;
        }
    }

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var prop))
        {
            return null;
        }
        return prop.ValueKind == JsonValueKind.String ? prop.GetString() : null;
    }
}
