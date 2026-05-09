namespace CopilotSessionManager.Core.Models;

/// <summary>
/// One checkpoint markdown file discovered under a session folder
/// (e.g. <c>~/.copilot/session-state/&lt;id&gt;/checkpoints/001-foo.md</c>).
/// Used by the README renderer to surface a session's history outline.
/// </summary>
public sealed record SessionCheckpointSummary(
    int Number,
    string Title,
    string FilePath);
