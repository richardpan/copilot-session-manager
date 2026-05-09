namespace CopilotSessionManager.Core.GitHub.Issues;

/// <summary>
/// Metadata for a GitHub issue resolved via <c>gh issue view</c>. The
/// <paramref name="Url"/> is the canonical
/// <c>https://github.com/&lt;owner&gt;/&lt;repo&gt;/issues/&lt;NN&gt;</c> form
/// so the UI can hand it directly to <see cref="Services.IFileLauncher"/>
/// without further normalisation.
/// </summary>
public sealed record IssueInfo(
    IssueRef Ref,
    string Title,
    IssueState State,
    string Url);
