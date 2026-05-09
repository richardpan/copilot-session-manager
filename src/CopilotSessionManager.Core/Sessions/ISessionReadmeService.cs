using System;
using System.Threading;
using System.Threading.Tasks;
using CopilotSessionManager.Core.Models;

namespace CopilotSessionManager.Core.Sessions;

/// <summary>
/// Orchestration around <see cref="ISessionReadmeRenderer"/> and
/// <see cref="ISessionReadmeStore"/>: ensures a session has a README on
/// disk, regenerates it on demand, and exposes the on-disk path for the UI
/// to launch with the system handler.
/// </summary>
public interface ISessionReadmeService
{
    /// <summary>The on-disk path the README will live at for <paramref name="sessionId"/>.</summary>
    string GetReadmePath(string sessionId);

    /// <summary>
    /// Renders the README for <paramref name="session"/>, splices in any
    /// existing user-editable blocks, writes it to disk if the content
    /// changed, and returns the final on-disk content.
    /// </summary>
    Task<string> EnsureAsync(
        Session session,
        SessionType label,
        CancellationToken cancellationToken = default);
}
