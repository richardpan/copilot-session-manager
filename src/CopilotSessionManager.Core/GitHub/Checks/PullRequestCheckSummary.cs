using System.Collections.Generic;

namespace CopilotSessionManager.Core.GitHub.Checks;

/// <summary>
/// Aggregated CI check status for a single pull request — the rollup
/// classification plus the names of the individual checks that drove it
/// into <see cref="PullRequestCheckRollup.Failure"/> or
/// <see cref="PullRequestCheckRollup.Pending"/> (so the UI can list them
/// in a tooltip).
/// </summary>
public sealed record PullRequestCheckSummary(
    PullRequestCheckRollup Rollup,
    IReadOnlyList<string> AttentionCheckNames);
