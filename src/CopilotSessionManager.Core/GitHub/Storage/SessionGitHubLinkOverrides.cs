namespace CopilotSessionManager.Core.GitHub.Storage;

/// <summary>
/// User-supplied overrides for the auto-detected GitHub links of a session.
/// Each property is independently overridable; <c>null</c> means "fall back to
/// the discovery output for this field". Persisted per session as JSON.
/// </summary>
/// <param name="RepositoryOverride">
/// Override for the repository slug or URL. When set, replaces the discovered
/// repository link. May be a canonical <c>owner/name</c> slug or a full
/// <c>https://github.com/...</c> URL — the consumer is responsible for any
/// normalization.
/// </param>
/// <param name="BranchOverride">
/// Override for the branch URL. When set, replaces the discovered branch link.
/// </param>
/// <param name="PullRequestNumberOverride">
/// User-assigned pull request number. When set, the UI surfaces this PR even
/// when no PR was auto-detected for the session's branch.
/// </param>
public sealed record SessionGitHubLinkOverrides(
    string? RepositoryOverride,
    string? BranchOverride,
    int? PullRequestNumberOverride)
{
    /// <summary>An empty overrides record with all fields <c>null</c>.</summary>
    public static readonly SessionGitHubLinkOverrides Empty = new(null, null, null);

    /// <summary>True when at least one field is set.</summary>
    public bool HasAnyOverride =>
        RepositoryOverride is not null
        || BranchOverride is not null
        || PullRequestNumberOverride is not null;
}
