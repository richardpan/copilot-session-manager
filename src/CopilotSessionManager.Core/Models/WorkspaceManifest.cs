namespace CopilotSessionManager.Core.Models;

/// <summary>
/// The contents of a session's <c>workspace.yaml</c>, as written by Copilot CLI.
/// </summary>
/// <remarks>
/// Field naming mirrors the YAML keys (snake_case) translated to PascalCase.
/// Unknown keys are tolerated by the adapter and discarded.
/// </remarks>
public sealed record WorkspaceManifest(
    string Id,
    string? Cwd,
    string? GitRoot,
    string? Repository,
    string? HostType,
    string? Branch,
    int SummaryCount,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? UpdatedAt,
    string? Summary);
