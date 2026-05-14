using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CopilotSessionManager.Core.Models;
using Microsoft.Extensions.Logging;

namespace CopilotSessionManager.Core.Sessions;

/// <summary>
/// Default <see cref="ISessionReadmeService"/>. Stateless — the only
/// persistent state is the README file itself.
/// </summary>
public sealed class SessionReadmeService : ISessionReadmeService
{
    private readonly ISessionReadmeRenderer _renderer;
    private readonly ISessionReadmeStore _store;
    private readonly ISessionFolderReader _folders;
    private readonly ISessionEventSummaryService? _events;
    private readonly ISubagentScanService? _subagents;
    private readonly ILogger<SessionReadmeService> _logger;

    public SessionReadmeService(
        ISessionReadmeRenderer renderer,
        ISessionReadmeStore store,
        ISessionFolderReader folders,
        ILogger<SessionReadmeService> logger,
        ISessionEventSummaryService? events = null,
        ISubagentScanService? subagents = null)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(folders);
        ArgumentNullException.ThrowIfNull(logger);
        _renderer = renderer;
        _store = store;
        _folders = folders;
        _events = events;
        _subagents = subagents;
        _logger = logger;
    }

    public string GetReadmePath(string sessionId) => _store.GetReadmePath(sessionId);

    public async Task<string> EnsureAsync(
        Session session,
        SessionType label,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        var checkpointsTask = _folders.GetCheckpointsAsync(session.Id, cancellationToken);
        var eventsTask = _events is null
            ? Task.FromResult(SessionEventSummary.Empty)
            : SafeScanEventsAsync(session.Id, cancellationToken);
        var subagentsTask = _subagents is null
            ? Task.FromResult<System.Collections.Generic.IReadOnlyList<SubagentSummary>>(Array.Empty<SubagentSummary>())
            : SafeScanSubagentsAsync(session.Id, cancellationToken);

        var checkpoints = await checkpointsTask.ConfigureAwait(false);
        var eventSummary = await eventsTask.ConfigureAwait(false);
        var subagents = await subagentsTask.ConfigureAwait(false);

        var ctx = new SessionReadmeContext(session, label, checkpoints, eventSummary, subagents);
        var rendered = _renderer.Render(ctx);

        try
        {
            return await _store.WriteAsync(session.Id, rendered, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write README for session {SessionId}.", session.Id);
            throw;
        }
    }

    private async Task<SessionEventSummary> SafeScanEventsAsync(string sessionId, CancellationToken ct)
    {
        try
        {
            return await _events!.ScanAsync(sessionId, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Event summary scan failed for session {SessionId}; rendering with empty summary.", sessionId);
            return SessionEventSummary.Empty;
        }
    }

    private async Task<System.Collections.Generic.IReadOnlyList<SubagentSummary>> SafeScanSubagentsAsync(string sessionId, CancellationToken ct)
    {
        try
        {
            return await _subagents!.ScanAsync(sessionId, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Sub-agent scan failed for session {SessionId}; rendering with empty list.", sessionId);
            return Array.Empty<SubagentSummary>();
        }
    }

    public async Task AppendAsync(
        string sessionId,
        string markdown,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(markdown);

        var path = _store.GetReadmePath(sessionId);
        var folder = Path.GetDirectoryName(path)!;

        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Could not create folder {Folder} to append README for session {SessionId}.", folder, sessionId);
            throw;
        }

        // If a README already exists, ensure there's a blank line separating
        // the existing content from the appended block. We deliberately do
        // not route through ISessionReadmeStore.WriteAsync because that
        // would re-run user-block splicing on raw text we never templated.
        var prefix = string.Empty;
        if (File.Exists(path))
        {
            try
            {
                var existing = await File.ReadAllTextAsync(path, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
                if (existing.Length > 0 && !existing.EndsWith('\n'))
                {
                    prefix = "\n\n";
                }
                else if (existing.Length > 0 && !existing.EndsWith("\n\n", StringComparison.Ordinal))
                {
                    prefix = "\n";
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Could not read existing README at {Path} before append; appending raw.", path);
            }
        }

        try
        {
            await File.AppendAllTextAsync(path, prefix + markdown, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to append to README for session {SessionId} at {Path}.", sessionId, path);
            throw;
        }
    }
}
