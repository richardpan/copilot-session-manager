using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CopilotSessionManager.Core.Sessions;

/// <summary>
/// App-owned per-session lifecycle flag (#?). Lets the user explicitly mark
/// a session as <see cref="SessionLifecycleState.Closed"/> when its work item
/// is truly wrapped up, distinct from the technical Copilot CLI process being
/// closed. Persists outside <c>~/.copilot/</c> per ADR-002 (no writes inside
/// the Copilot state directory).
/// </summary>
/// <remarks>
/// "Not present" and "explicitly active" are equivalent — the store only
/// persists session ids that the user has explicitly marked as Closed. This
/// keeps the on-disk file small and means freshly-discovered sessions are
/// implicitly Active without any extra bookkeeping.
/// </remarks>
public interface ISessionLifecycleStore
{
    /// <summary>The current lifecycle state for <paramref name="sessionId"/>.</summary>
    Task<SessionLifecycleState> GetAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>The set of all session ids that are explicitly Closed.</summary>
    Task<IReadOnlySet<string>> GetClosedAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets the lifecycle state for <paramref name="sessionId"/>. Idempotent;
    /// raises <see cref="LifecycleChanged"/> only when the persisted state
    /// actually changes.
    /// </summary>
    Task SetAsync(string sessionId, SessionLifecycleState state, CancellationToken cancellationToken = default);

    /// <summary>
    /// Raised when a session's lifecycle state changes. Subscribers may be
    /// invoked from a background thread.
    /// </summary>
    event EventHandler<SessionLifecycleChangedEventArgs>? LifecycleChanged;
}

/// <summary>Payload for <see cref="ISessionLifecycleStore.LifecycleChanged"/>.</summary>
public sealed class SessionLifecycleChangedEventArgs : EventArgs
{
    public SessionLifecycleChangedEventArgs(string sessionId, SessionLifecycleState state)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        SessionId = sessionId;
        State = state;
    }

    public string SessionId { get; }

    /// <summary>The new lifecycle state.</summary>
    public SessionLifecycleState State { get; }
}
