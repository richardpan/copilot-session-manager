namespace CopilotSessionManager.Core.Models;

/// <summary>
/// A Copilot session as surfaced to the application — combines facts from
/// <c>session-store.db</c>, <c>workspace.yaml</c>, lock files, and the events
/// stream into a single immutable view.
/// </summary>
public sealed record Session(
    string Id,
    string? Cwd,
    string? Repository,
    string? Branch,
    string? Summary,
    string? HostType,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int TurnCount,
    SessionStatus Status,
    CopilotVersion CopilotVersion);
