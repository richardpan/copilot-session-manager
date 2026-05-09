namespace CopilotSessionManager.Core.GitHub;

/// <summary>
/// Tracks whether GitHub-backed features are currently usable. Updated
/// purely from real CLI invocations (no background polling) — every
/// <see cref="GhCliGitHubClient"/> call reports its outcome via
/// <see cref="Report"/>. View models subscribe to
/// <see cref="AvailabilityChanged"/> and surface a banner / disabled state.
/// </summary>
public interface IGitHubAvailabilityProvider
{
    /// <summary>The latest known state. Never <c>null</c>.</summary>
    GitHubAvailabilityState Current { get; }

    /// <summary>
    /// Raised <em>only on transitions</em> (i.e., when <see cref="Current"/>
    /// changes). Identical follow-up reports are debounced.
    /// </summary>
    event EventHandler<GitHubAvailabilityState>? AvailabilityChanged;

    /// <summary>
    /// Records the outcome of a GitHub interaction. Successful calls should
    /// pass <see cref="GitHubAvailability.Available"/> with a <c>null</c>
    /// message so the provider can recover from a previous Offline /
    /// Unauthenticated state.
    /// </summary>
    void Report(GitHubAvailability state, string? userMessage = null);
}
