using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CopilotSessionManager.Core.Models;

namespace CopilotSessionManager.Core.Sessions;

/// <summary>
/// App-owned store for the user's <see cref="SessionType"/> assignment per
/// session id. Persisted outside <c>~/.copilot/</c> per ADR-002.
/// </summary>
/// <remarks>
/// Session ids that have not been explicitly labeled return
/// <see cref="SessionType.Exploratory"/> from <see cref="GetAsync"/>; they do
/// not appear in <see cref="GetAllAsync"/>.
/// </remarks>
public interface ISessionLabelStore
{
    /// <summary>The label assigned to <paramref name="sessionId"/>, or
    /// <see cref="SessionType.Exploratory"/> if none was set.</summary>
    Task<SessionType> GetAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>All explicitly-labeled session ids and their assignments.</summary>
    Task<IReadOnlyDictionary<string, SessionType>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Assigns <paramref name="type"/> to <paramref name="sessionId"/>. Raises
    /// <see cref="LabelChanged"/> if the value differs from the current one.
    /// </summary>
    Task SetAsync(string sessionId, SessionType type, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes the label assignment, returning the session to the default
    /// <see cref="SessionType.Exploratory"/>. Raises <see cref="LabelChanged"/>
    /// if a label was actually removed.
    /// </summary>
    Task RemoveAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Raised when a session's label changes (set, updated, or removed).
    /// Subscribers may be invoked from a background thread.
    /// </summary>
    event EventHandler<SessionLabelChangedEventArgs>? LabelChanged;
}

/// <summary>Payload for <see cref="ISessionLabelStore.LabelChanged"/>.</summary>
public sealed class SessionLabelChangedEventArgs : EventArgs
{
    public SessionLabelChangedEventArgs(string sessionId, SessionType newType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        SessionId = sessionId;
        NewType = newType;
    }

    public string SessionId { get; }

    /// <summary>The new effective label (defaults to
    /// <see cref="SessionType.Exploratory"/> after a removal).</summary>
    public SessionType NewType { get; }
}
