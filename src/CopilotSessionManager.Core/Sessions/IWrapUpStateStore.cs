using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CopilotSessionManager.Core.Sessions;

/// <summary>
/// Persists per-session "wrap-up requested at" timestamps for the V1.3
/// (#149) 📝 Wrap up launcher button. Stored outside <c>~/.copilot/</c>
/// per ADR-002. A session that has been "wrap-up requested" hides its
/// badge until its <c>UpdatedAt</c> advances past the recorded timestamp
/// (i.e. the user has produced a fresh event since wrap-up was requested).
/// </summary>
public interface IWrapUpStateStore
{
    /// <summary>
    /// Returns the timestamp at which wrap-up was last requested for
    /// <paramref name="sessionId"/>, or <c>null</c> if no request has
    /// been recorded.
    /// </summary>
    Task<DateTimeOffset?> GetRequestedAtAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>Snapshot of every recorded wrap-up timestamp.</summary>
    Task<IReadOnlyDictionary<string, DateTimeOffset>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Records that wrap-up was requested for <paramref name="sessionId"/>
    /// at <paramref name="requestedAt"/>. Idempotent within the same
    /// timestamp — calling twice with the same value still raises
    /// <see cref="WrapUpStateChanged"/> so subscribers can refresh their
    /// projections.
    /// </summary>
    Task MarkRequestedAsync(string sessionId, DateTimeOffset requestedAt, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes any recorded wrap-up timestamp for <paramref name="sessionId"/>.
    /// No-op if the session was not present. Raises
    /// <see cref="WrapUpStateChanged"/> only when the state actually changes.
    /// </summary>
    Task ClearAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Raised when a session's wrap-up timestamp is recorded or cleared.
    /// Subscribers may be invoked from a background thread.
    /// </summary>
    event EventHandler<WrapUpStateChangedEventArgs>? WrapUpStateChanged;
}

/// <summary>Payload for <see cref="IWrapUpStateStore.WrapUpStateChanged"/>.</summary>
public sealed class WrapUpStateChangedEventArgs : EventArgs
{
    public WrapUpStateChangedEventArgs(string sessionId, DateTimeOffset? requestedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        SessionId = sessionId;
        RequestedAt = requestedAt;
    }

    public string SessionId { get; }

    /// <summary>The new wrap-up timestamp, or <c>null</c> if cleared.</summary>
    public DateTimeOffset? RequestedAt { get; }
}
