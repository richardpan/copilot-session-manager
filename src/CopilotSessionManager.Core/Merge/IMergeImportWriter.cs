using System.Threading;
using System.Threading.Tasks;

namespace CopilotSessionManager.Core.Merge;

/// <summary>
/// Writes a markdown transcript exported from one session into another
/// session's folder so the CLI picks it up on next resume.
/// </summary>
/// <remarks>
/// We don't write directly into <c>session-store.db</c> in V1 because the
/// store is opened read-only by the rest of the app and the Copilot CLI
/// schema is not part of our compatibility contract. Instead we drop the
/// markdown into a sibling <c>merge-imports/</c> folder under the target
/// session — visible from the README and resumable as a manual paste.
/// </remarks>
public interface IMergeImportWriter
{
    /// <summary>
    /// Writes <paramref name="markdown"/> as a new merge-import file under
    /// <paramref name="targetSessionId"/>'s session folder. Returns the
    /// absolute path of the file written.
    /// </summary>
    Task<string> WriteAsync(
        string targetSessionId,
        string sourceSessionId,
        string markdown,
        CancellationToken cancellationToken = default);
}
