namespace CopilotSessionManager.Core.Sessions;

/// <summary>
/// Raw row from the Copilot CLI's <c>session-store.db</c> <c>sessions</c>
/// table, joined with a <c>turns</c> count.
/// </summary>
public sealed record SessionStoreRecord(
    string Id,
    string? Cwd,
    string? Repository,
    string? Branch,
    string? Summary,
    string? HostType,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int TurnCount);
