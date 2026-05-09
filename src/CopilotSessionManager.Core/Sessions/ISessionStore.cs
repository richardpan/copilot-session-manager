namespace CopilotSessionManager.Core.Sessions;

/// <summary>
/// Read-only access to the Copilot CLI's <c>session-store.db</c>.
/// </summary>
public interface ISessionStore
{
    /// <summary>
    /// Enumerate every session row in the database, ordered by
    /// <c>updated_at</c> descending. Returns an empty list if the database
    /// file does not exist yet.
    /// </summary>
    Task<IReadOnlyList<SessionStoreRecord>> ListAsync(CancellationToken cancellationToken = default);
}
