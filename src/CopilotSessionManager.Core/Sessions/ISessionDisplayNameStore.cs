using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CopilotSessionManager.Core.Sessions;

/// <summary>
/// App-owned per-session display-name override (#105). Persists outside
/// <c>~/.copilot/</c> per ADR-002. Returning <c>null</c> means "no override —
/// fall back to the Copilot-assigned title".
/// </summary>
/// <remarks>
/// Sessions that have not been explicitly renamed return <c>null</c> from
/// <see cref="GetAsync"/>; they do not appear in <see cref="GetAllAsync"/>.
/// Setting an empty / whitespace-only value clears the override.
/// </remarks>
public interface ISessionDisplayNameStore
{
    /// <summary>
    /// The override assigned to <paramref name="sessionId"/>, or <c>null</c>
    /// if none was set.
    /// </summary>
    Task<string?> GetAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>All sessions with an explicit display-name override.</summary>
    Task<IReadOnlyDictionary<string, string>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets <paramref name="displayName"/> as the override for
    /// <paramref name="sessionId"/>. Passing <c>null</c> / whitespace clears
    /// the override (equivalent to <see cref="RemoveAsync"/>).
    /// Raises <see cref="DisplayNameChanged"/> if the value differs from the
    /// current one.
    /// </summary>
    Task SetAsync(string sessionId, string? displayName, CancellationToken cancellationToken = default);

    /// <summary>Removes any override. No-op when none exists.</summary>
    Task RemoveAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Raised when a session's override changes (set, updated, or removed).
    /// Subscribers may be invoked from a background thread.
    /// </summary>
    event EventHandler<SessionDisplayNameChangedEventArgs>? DisplayNameChanged;
}

/// <summary>Payload for <see cref="ISessionDisplayNameStore.DisplayNameChanged"/>.</summary>
public sealed class SessionDisplayNameChangedEventArgs : EventArgs
{
    public SessionDisplayNameChangedEventArgs(string sessionId, string? newDisplayName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        SessionId = sessionId;
        NewDisplayName = newDisplayName;
    }

    public string SessionId { get; }

    /// <summary>The new override; <c>null</c> if it was removed.</summary>
    public string? NewDisplayName { get; }
}
