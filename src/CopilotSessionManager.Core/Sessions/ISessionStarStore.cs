using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CopilotSessionManager.Core.Sessions;

/// <summary>
/// App-owned per-session "star" flag (#112). A starred session is pinned to
/// the top of the dashboard regardless of activity / updated time. Persists
/// outside <c>~/.copilot/</c> per ADR-002 (no writes inside the Copilot
/// state directory).
/// </summary>
/// <remarks>
/// "Not present" and "explicitly unstarred" are equivalent. Starring a
/// session is idempotent (setting an already-starred id is a no-op and does
/// not raise <see cref="StarsChanged"/>).
/// </remarks>
public interface ISessionStarStore
{
    /// <summary>True when <paramref name="sessionId"/> is currently starred.</summary>
    Task<bool> IsStarredAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>The set of all currently-starred session ids.</summary>
    Task<IReadOnlySet<string>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Pins <paramref name="sessionId"/>. No-op if already starred. Raises
    /// <see cref="StarsChanged"/> only when the state actually changes.
    /// </summary>
    Task SetAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Unpins <paramref name="sessionId"/>. No-op if not starred. Raises
    /// <see cref="StarsChanged"/> only when the state actually changes.
    /// </summary>
    Task RemoveAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Raised when a session's starred state changes (set or removed).
    /// Subscribers may be invoked from a background thread.
    /// </summary>
    event EventHandler<SessionStarChangedEventArgs>? StarsChanged;
}

/// <summary>Payload for <see cref="ISessionStarStore.StarsChanged"/>.</summary>
public sealed class SessionStarChangedEventArgs : EventArgs
{
    public SessionStarChangedEventArgs(string sessionId, bool isStarred)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        SessionId = sessionId;
        IsStarred = isStarred;
    }

    public string SessionId { get; }

    /// <summary>The new star state.</summary>
    public bool IsStarred { get; }
}
