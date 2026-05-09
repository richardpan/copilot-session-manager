namespace CopilotSessionManager.Core.Models;

/// <summary>
/// A pull request associated with a session's working branch.
/// </summary>
public sealed record PullRequestInfo(
    int Number,
    string Title,
    PullRequestState State,
    string Url);
