using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using CopilotSessionManager.Core.Cli.Share;
using CopilotSessionManager.Core.Sessions;
using Microsoft.Extensions.Logging;

namespace CopilotSessionManager.Core.Merge;

/// <summary>
/// Default <see cref="ISessionMerger"/>. Wires
/// <see cref="ICopilotShareInvoker"/> → <see cref="IMergeImportWriter"/> →
/// <see cref="ISessionReadmeService.AppendAsync"/> together.
/// </summary>
/// <remarks>
/// README append failures are intentionally non-fatal: the markdown has
/// already been written into the target session folder, so the merge has
/// effectively succeeded. We log a warning and return Success=true with a
/// null <see cref="MergeResult.MergeNote"/> so callers can surface a softer
/// notice if they want to.
/// </remarks>
public sealed class SessionMerger : ISessionMerger
{
    private readonly ICopilotShareInvoker _share;
    private readonly IMergeImportWriter _importer;
    private readonly ISessionReadmeService _readme;
    private readonly TimeProvider _clock;
    private readonly ILogger<SessionMerger> _logger;

    public SessionMerger(
        ICopilotShareInvoker share,
        IMergeImportWriter importer,
        ISessionReadmeService readme,
        TimeProvider clock,
        ILogger<SessionMerger> logger)
    {
        ArgumentNullException.ThrowIfNull(share);
        ArgumentNullException.ThrowIfNull(importer);
        ArgumentNullException.ThrowIfNull(readme);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);
        _share = share;
        _importer = importer;
        _readme = readme;
        _clock = clock;
        _logger = logger;
    }

    public async Task<MergeResult> MergeAsync(
        string sourceSessionId,
        string targetSessionId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourceSessionId))
        {
            return MergeResult.Fail("Source session id is required.");
        }
        if (string.IsNullOrWhiteSpace(targetSessionId))
        {
            return MergeResult.Fail("Target session id is required.");
        }
        if (string.Equals(sourceSessionId, targetSessionId, StringComparison.Ordinal))
        {
            return MergeResult.Fail("Source and target session ids must be different.");
        }

        _logger.LogInformation(
            "Starting merge of source session {SourceId} into target session {TargetId}.",
            sourceSessionId,
            targetSessionId);

        var share = await _share.ExportAsync(sourceSessionId, cancellationToken).ConfigureAwait(false);
        if (!share.Success || string.IsNullOrEmpty(share.Markdown))
        {
            var msg = share.ErrorMessage ?? "copilot --share failed.";
            _logger.LogWarning(
                "Merge aborted: source export failed for session {SourceId}: {Error}",
                sourceSessionId,
                msg);
            return MergeResult.Fail($"Could not export source session: {msg}");
        }

        try
        {
            await _importer
                .WriteAsync(targetSessionId, sourceSessionId, share.Markdown, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Merge aborted: could not write merge import into target session {TargetId}.",
                targetSessionId);
            return MergeResult.Fail($"Could not write merge import into target session: {ex.Message}");
        }

        var note = BuildMergeNote(sourceSessionId, _clock.GetUtcNow());
        try
        {
            await _readme.AppendAsync(targetSessionId, note, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Merge import succeeded but README append failed for target session {TargetId}; continuing.",
                targetSessionId);
            return MergeResult.Ok(mergeNote: null);
        }

        _logger.LogInformation(
            "Merge complete: source {SourceId} → target {TargetId}.",
            sourceSessionId,
            targetSessionId);
        return MergeResult.Ok(note);
    }

    /// <summary>
    /// Builds the markdown header appended to the target session's
    /// <c>SESSION-README.md</c> on a successful merge. Public so callers
    /// (e.g. UI surfaces) can preview the exact text before committing.
    /// </summary>
    public static string BuildMergeNote(string sourceSessionId, DateTimeOffset utcNow)
    {
        var stamp = utcNow.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);
        return $"## Merged from session `{sourceSessionId}` on {stamp}\n";
    }
}
