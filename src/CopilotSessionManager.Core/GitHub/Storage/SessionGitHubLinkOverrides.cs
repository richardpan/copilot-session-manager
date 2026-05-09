using System;
using System.Collections.Generic;

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
    /// <summary>An empty overrides record with all fields <c>null</c> / empty.</summary>
    public static readonly SessionGitHubLinkOverrides Empty = new(null, null, null);

    /// <summary>
    /// User-linked GitHub issue refs in canonical <c>owner/repo#NN</c> form
    /// (lower-cased owner/repo). The order is preserved as the user added them
    /// and duplicates are filtered by <see cref="ISessionGitHubLinksStore"/>
    /// when persisting. Defaults to an empty list so v1 documents (which had
    /// no issue refs) deserialise cleanly.
    /// </summary>
    public IReadOnlyList<string> IssueRefs { get; init; } = Array.Empty<string>();

    /// <summary>True when at least one field is set.</summary>
    public bool HasAnyOverride =>
        RepositoryOverride is not null
        || BranchOverride is not null
        || PullRequestNumberOverride is not null
        || IssueRefs.Count > 0;
}
