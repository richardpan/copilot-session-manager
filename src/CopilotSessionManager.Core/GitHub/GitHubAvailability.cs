namespace CopilotSessionManager.Core.GitHub;

/// <summary>
/// Coarse-grained availability classification for GitHub-backed features
/// (PR sync, branch lookups, etc.). Intentionally small — UI just needs to
/// know whether to grey things out and what to tell the user.
/// </summary>
public enum GitHubAvailability
{
    /// <summary>The last GitHub call succeeded; assume features work.</summary>
    Available,

    /// <summary>The <c>gh</c> CLI is reachable but not signed in.</summary>
    Unauthenticated,

    /// <summary>Network appears down (DNS / TLS / connection refused).</summary>
    Offline,
}

/// <summary>
/// Snapshot of the most recently observed GitHub availability, suitable for
/// pushing to view models and for showing a banner. Immutable.
/// </summary>
/// <param name="State">Coarse classification.</param>
/// <param name="UserMessage">
/// Short, human-friendly explanation safe to display in the UI. <c>null</c>
/// when <paramref name="State"/> is <see cref="GitHubAvailability.Available"/>.
/// </param>
/// <param name="DetectedAt">When the state was observed.</param>
public sealed record GitHubAvailabilityState(
    GitHubAvailability State,
    string? UserMessage,
    DateTimeOffset DetectedAt)
{
    /// <summary>The default "everything is fine" state used at startup.</summary>
    public static GitHubAvailabilityState InitialAvailable(DateTimeOffset detectedAt) =>
        new(GitHubAvailability.Available, null, detectedAt);
}
