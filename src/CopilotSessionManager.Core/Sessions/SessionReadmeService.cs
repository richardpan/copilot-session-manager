using System;
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
    private readonly ILogger<SessionReadmeService> _logger;

    public SessionReadmeService(
        ISessionReadmeRenderer renderer,
        ISessionReadmeStore store,
        ISessionFolderReader folders,
        ILogger<SessionReadmeService> logger)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(folders);
        ArgumentNullException.ThrowIfNull(logger);
        _renderer = renderer;
        _store = store;
        _folders = folders;
        _logger = logger;
    }

    public string GetReadmePath(string sessionId) => _store.GetReadmePath(sessionId);

    public async Task<string> EnsureAsync(
        Session session,
        SessionType label,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        var checkpoints = await _folders.GetCheckpointsAsync(session.Id, cancellationToken)
            .ConfigureAwait(false);
        var ctx = new SessionReadmeContext(session, label, checkpoints);
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
}
