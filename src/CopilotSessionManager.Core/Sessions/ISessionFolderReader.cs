using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CopilotSessionManager.Core.Models;

namespace CopilotSessionManager.Core.Sessions;

/// <summary>
/// Best-effort reader for content that lives inside a Copilot session folder
/// (<c>~/.copilot/session-state/&lt;id&gt;/</c>). All methods are tolerant: if
/// the folder or file is missing they return empty results rather than throw.
/// </summary>
public interface ISessionFolderReader
{
    /// <summary>The path the reader resolves for <paramref name="sessionId"/>.</summary>
    string GetSessionFolderPath(string sessionId);

    /// <summary>
    /// Lists checkpoint markdown files under <c>checkpoints/</c> in number order.
    /// Returns an empty list when no checkpoints folder exists.
    /// </summary>
    Task<IReadOnlyList<SessionCheckpointSummary>> GetCheckpointsAsync(
        string sessionId,
        CancellationToken cancellationToken = default);
}
