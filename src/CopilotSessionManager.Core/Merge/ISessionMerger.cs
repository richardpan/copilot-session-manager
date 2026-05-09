using System.Threading;
using System.Threading.Tasks;

namespace CopilotSessionManager.Core.Merge;

/// <summary>
/// Outcome of a session merge attempt.
/// </summary>
/// <param name="Success">True when the source transcript was exported and
/// imported into the target session. README append failures do not flip this
/// to false (they are logged as warnings).</param>
/// <param name="ErrorMessage">On failure, the first user-facing error (from
/// either the share invoker or the import writer).</param>
/// <param name="MergeNote">On success, the markdown header the merge added
/// to the target's README, e.g. <c>## Merged from session abc123 on …</c>.
/// Null when README append failed.</param>
public sealed record MergeResult(bool Success, string? ErrorMessage, string? MergeNote)
{
    /// <summary>Builds a successful result.</summary>
    public static MergeResult Ok(string? mergeNote) =>
        new(Success: true, ErrorMessage: null, MergeNote: mergeNote);

    /// <summary>Builds a failed result with the given error message.</summary>
    public static MergeResult Fail(string errorMessage) =>
        new(Success: false, ErrorMessage: errorMessage, MergeNote: null);
}

/// <summary>
/// Combines the transcript of a source Copilot session into a target
/// session. The target session picks up the imported markdown on its next
/// resume; the source session is left untouched.
/// </summary>
public interface ISessionMerger
{
    /// <summary>
    /// Exports <paramref name="sourceSessionId"/> via
    /// <see cref="Cli.Share.ICopilotShareInvoker"/> and writes the markdown
    /// into <paramref name="targetSessionId"/>'s session folder. Best-effort
    /// appends a <c>## Merged from session …</c> section to the target
    /// session's <c>SESSION-README.md</c>.
    /// </summary>
    Task<MergeResult> MergeAsync(
        string sourceSessionId,
        string targetSessionId,
        CancellationToken cancellationToken = default);
}
