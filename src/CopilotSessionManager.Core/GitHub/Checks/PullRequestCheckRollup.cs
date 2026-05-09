namespace CopilotSessionManager.Core.GitHub.Checks;

/// <summary>
/// Coarse-grained rollup of all CI checks attached to the latest commit on
/// a pull request, as projected from <c>gh pr checks</c>'s per-check
/// <c>bucket</c> field.
/// </summary>
/// <remarks>
/// Mapping rules (highest precedence first, matching what the GitHub UI
/// itself does in its PR header pill):
/// <list type="bullet">
///   <item><c>fail</c>, <c>cancel</c>, <c>action_required</c>, <c>stale</c>,
///   <c>timeout</c>, <c>error</c> ⇒ <see cref="Failure"/>.</item>
///   <item><c>pending</c>, <c>queued</c>, <c>in_progress</c> ⇒
///   <see cref="Pending"/>.</item>
///   <item>All checks <c>pass</c> / <c>skipping</c> / <c>neutral</c> ⇒
///   <see cref="Success"/>.</item>
///   <item>No checks at all ⇒ <see cref="None"/>.</item>
/// </list>
/// </remarks>
public enum PullRequestCheckRollup
{
    /// <summary>No checks have run / no commit-status data available.</summary>
    None = 0,

    /// <summary>At least one check is queued or running, none have failed.</summary>
    Pending,

    /// <summary>All checks completed successfully (or were neutral / skipped).</summary>
    Success,

    /// <summary>At least one check failed, errored, was cancelled, or timed out.</summary>
    Failure,
}
