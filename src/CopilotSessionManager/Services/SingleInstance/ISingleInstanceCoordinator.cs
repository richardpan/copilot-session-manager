using System;
using System.Threading;
using System.Threading.Tasks;

namespace CopilotSessionManager.Services.SingleInstance;

/// <summary>
/// Coordinates the single-instance contract for the application. The first
/// process to <see cref="TryAcquireAsync"/> wins ownership and starts a
/// background listener. Any subsequent process whose acquisition attempt
/// fails has, as a side effect, already signaled the owner — the owner
/// raises <see cref="ActivationRequested"/> and is expected to surface its
/// main window.
/// </summary>
public interface ISingleInstanceCoordinator : IDisposable
{
    /// <summary>
    /// Attempts to claim ownership for this process. Returns <c>true</c> on
    /// success (this process is now the owner). Returns <c>false</c> if
    /// another instance already holds the lock — in that case the
    /// implementation has already signaled the owner via the IPC channel.
    /// </summary>
    Task<bool> TryAcquireAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Raised on the owner process when another instance pings it. Always
    /// raised on a thread-pool thread; subscribers must marshal to the UI
    /// thread themselves.
    /// </summary>
    event EventHandler? ActivationRequested;
}
