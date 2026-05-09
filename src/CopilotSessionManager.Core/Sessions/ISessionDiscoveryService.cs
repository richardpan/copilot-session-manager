using CopilotSessionManager.Core.Models;

namespace CopilotSessionManager.Core.Sessions;

/// <summary>
/// Combines the <c>session-store.db</c> rows with on-disk <c>session-state/</c>
/// directories (workspace.yaml + events.jsonl + lock files) and emits a single
/// <see cref="Session"/> view.
/// </summary>
public interface ISessionDiscoveryService : IAsyncDisposable
{
    /// <summary>Most recently scanned snapshot. Empty until <see cref="ScanAsync"/> runs.</summary>
    IReadOnlyList<Session> CurrentSessions { get; }

    /// <summary>Scan the filesystem and database, returning a fresh snapshot.</summary>
    Task<IReadOnlyList<Session>> ScanAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Begin watching the <c>session-store.db</c> file and the
    /// <c>session-state/</c> directory; raise <see cref="SessionsChanged"/>
    /// after a debounced rescan when changes occur.
    /// </summary>
    Task StartWatchingAsync(CancellationToken cancellationToken = default);

    /// <summary>Stop watching for changes (does not clear current sessions).</summary>
    Task StopWatchingAsync();

    /// <summary>Raised after a rescan triggered by an underlying file change.</summary>
    event EventHandler<SessionsChangedEventArgs>? SessionsChanged;
}

/// <summary>
/// Snapshot delivered to <see cref="ISessionDiscoveryService.SessionsChanged"/>.
/// </summary>
public sealed class SessionsChangedEventArgs : EventArgs
{
    public SessionsChangedEventArgs(IReadOnlyList<Session> sessions)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        Sessions = sessions;
    }

    public IReadOnlyList<Session> Sessions { get; }
}
