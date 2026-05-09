using System.Threading;
using System.Threading.Tasks;

namespace CopilotSessionManager.Core.Cli.Share;

/// <summary>
/// Result of asking the Copilot CLI to export a session transcript via
/// <c>copilot --resume &lt;id&gt; --share=&lt;path&gt;</c>.
/// </summary>
/// <param name="Success">True when the CLI exited 0 and a non-empty markdown
/// file was produced.</param>
/// <param name="MarkdownPath">On success, the temp-file path that received
/// the markdown. Callers own deletion (the invoker never deletes it because
/// downstream consumers may want to copy it elsewhere first).</param>
/// <param name="Markdown">On success, the markdown content read back from
/// <see cref="MarkdownPath"/>.</param>
/// <param name="ErrorMessage">On failure, a user-facing description of what
/// went wrong (CLI missing, non-zero exit, timeout, empty output, …).</param>
public sealed record ShareResult(
    bool Success,
    string? MarkdownPath,
    string? Markdown,
    string? ErrorMessage)
{
    /// <summary>Builds a successful result.</summary>
    public static ShareResult Ok(string markdownPath, string markdown) =>
        new(Success: true, MarkdownPath: markdownPath, Markdown: markdown, ErrorMessage: null);

    /// <summary>Builds a failed result with the given error message.</summary>
    public static ShareResult Fail(string errorMessage) =>
        new(Success: false, MarkdownPath: null, Markdown: null, ErrorMessage: errorMessage);
}

/// <summary>
/// Wraps the Copilot CLI's <c>--share</c> flag. Given a session id, asks the
/// CLI to dump the resumed transcript as markdown into a caller-managed temp
/// file and returns the contents.
/// </summary>
/// <remarks>
/// Implementations must never throw for ordinary failure modes (CLI missing,
/// non-zero exit, timeout, empty output). They must instead return a
/// <see cref="ShareResult"/> with <c>Success=false</c> and a classified
/// error message.
/// </remarks>
public interface ICopilotShareInvoker
{
    /// <summary>
    /// Invokes <c>copilot --resume &lt;sessionId&gt; --share=&lt;temp&gt;</c>
    /// and returns the captured markdown.
    /// </summary>
    Task<ShareResult> ExportAsync(string sessionId, CancellationToken cancellationToken = default);
}
