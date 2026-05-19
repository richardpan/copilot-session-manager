using System.Threading;
using System.Threading.Tasks;
using CopilotSessionManager.Core.Models;

namespace CopilotSessionManager.Core.Sessions;

/// <summary>
/// V1.6 (#118): Owns the brand-new <c>SESSION-DOCS.md</c> + <c>SESSION-DOCS.html</c>
/// pair that lives in every session folder. csm scaffolds the markdown file
/// once and then never touches it again — the user (or the user's agent)
/// owns all subsequent edits. csm regenerates the HTML view on demand
/// whenever any source under the session folder is newer than the
/// rendered <c>.html</c>.
/// </summary>
/// <remarks>
/// This service is intentionally separate from <see cref="ISessionReadmeService"/>:
/// SESSION-README.md remains csm-managed (auto-generated on every change),
/// while SESSION-DOCS.md is a curated narrative that csm scaffolds and
/// then leaves alone. The two files coexist; the prominent UI surface
/// shipped in V1.6 launches the HTML rendered from this service.
/// </remarks>
public interface ISessionDocsService
{
    /// <summary>The on-disk path to <c>SESSION-DOCS.md</c> for the session.</summary>
    string GetDocsMarkdownPath(string sessionId);

    /// <summary>The on-disk path to <c>SESSION-DOCS.html</c> for the session.</summary>
    string GetDocsHtmlPath(string sessionId);

    /// <summary>
    /// V1.5: the on-disk path to <c>plan.md</c> for the session — the
    /// agent-curated planning file that Copilot CLI itself maintains
    /// across the session lifetime. csm never writes to this file.
    /// </summary>
    /// <remarks>
    /// The dashboard's "📚 Docs" surface prefers this over the older
    /// SESSION-DOCS.html when the file exists, since it reflects the
    /// agent's latest thinking without requiring a regen step.
    /// </remarks>
    string GetPlanMarkdownPath(string sessionId);

    /// <summary>
    /// Scaffolds <c>SESSION-DOCS.md</c> if it does not yet exist (templated
    /// content with H2 sections + a top-of-file comment that explicitly
    /// tells any agent it is safe to edit). Never overwrites an existing
    /// file. Then regenerates <c>SESSION-DOCS.html</c> if it is missing or
    /// stale (any source mtime newer than the .html). Returns the path to
    /// the rendered HTML.
    /// </summary>
    /// <param name="session">Session metadata used to seed the header (display name, repo, branch, status).</param>
    /// <param name="cancellationToken">Cooperative cancellation token.</param>
    /// <returns>Absolute path to the (now-current) <c>SESSION-DOCS.html</c>.</returns>
    Task<string> EnsureAsync(Session session, CancellationToken cancellationToken = default);
}
