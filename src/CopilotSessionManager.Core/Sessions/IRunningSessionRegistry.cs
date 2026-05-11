namespace CopilotSessionManager.Core.Sessions;

/// <summary>
/// In-memory tracker of <c>pwsh.exe</c> processes that the app launched for a
/// given Copilot session id (#104). Used by the dashboard's "Open" button so
/// a second click on the same session brings the existing window forward
/// instead of spawning a duplicate.
/// </summary>
/// <remarks>
/// Tracking is intentionally not persisted across CSM restarts in V1.1 — on
/// restart, all sessions appear unbound and the next click launches a new
/// window. Restoring the mapping by scanning <c>pwsh</c> command lines is a
/// follow-up.
/// </remarks>
public interface IRunningSessionRegistry
{
    /// <summary>
    /// Records that <paramref name="processId"/> is the PID of the
    /// <c>pwsh.exe</c> launched for <paramref name="sessionId"/>. Replaces
    /// any previously-tracked PID for that session id.
    /// </summary>
    void Register(string sessionId, int processId);

    /// <summary>
    /// Returns the PID associated with <paramref name="sessionId"/>, or
    /// <c>null</c> if no PID has been registered.
    /// </summary>
    int? TryGetProcessId(string sessionId);

    /// <summary>Removes any tracked PID for <paramref name="sessionId"/>.</summary>
    void Unregister(string sessionId);
}

/// <summary>
/// Default in-memory <see cref="IRunningSessionRegistry"/>. Backed by a
/// <see cref="System.Collections.Concurrent.ConcurrentDictionary{TKey, TValue}"/>
/// so it can be used safely from any thread.
/// </summary>
public sealed class InMemoryRunningSessionRegistry : IRunningSessionRegistry
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _byId =
        new(System.StringComparer.OrdinalIgnoreCase);

    public void Register(string sessionId, int processId)
    {
        System.ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (processId <= 0)
        {
            throw new System.ArgumentOutOfRangeException(nameof(processId), "PID must be positive.");
        }
        _byId[sessionId] = processId;
    }

    public int? TryGetProcessId(string sessionId)
    {
        System.ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        return _byId.TryGetValue(sessionId, out var pid) ? pid : null;
    }

    public void Unregister(string sessionId)
    {
        System.ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        _byId.TryRemove(sessionId, out _);
    }
}
