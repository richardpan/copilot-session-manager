using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CopilotSessionManager.Core.GitHub.Storage;
using Microsoft.Extensions.Logging;

namespace CopilotSessionManager.Core.Sessions;

/// <inheritdoc />
public sealed class SessionDeletionService : ISessionDeletionService
{
    private readonly ISessionFolderReader _folders;
    private readonly ISessionDisplayNameStore? _displayNames;
    private readonly ISessionLabelStore? _labels;
    private readonly ISessionGitHubLinksStore? _githubLinks;
    private readonly IRunningSessionRegistry? _registry;
    private readonly IDeletedSessionRegistry? _tombstones;
    private readonly ILogger<SessionDeletionService> _logger;

    public SessionDeletionService(
        ISessionFolderReader folders,
        ILogger<SessionDeletionService> logger)
        : this(folders, displayNames: null, labels: null, githubLinks: null, registry: null, logger)
    {
    }

    public SessionDeletionService(
        ISessionFolderReader folders,
        ISessionDisplayNameStore? displayNames,
        ISessionLabelStore? labels,
        ISessionGitHubLinksStore? githubLinks,
        IRunningSessionRegistry? registry,
        ILogger<SessionDeletionService> logger)
        : this(folders, displayNames, labels, githubLinks, registry, tombstones: null, logger)
    {
    }

    public SessionDeletionService(
        ISessionFolderReader folders,
        ISessionDisplayNameStore? displayNames,
        ISessionLabelStore? labels,
        ISessionGitHubLinksStore? githubLinks,
        IRunningSessionRegistry? registry,
        IDeletedSessionRegistry? tombstones,
        ILogger<SessionDeletionService> logger)
    {
        ArgumentNullException.ThrowIfNull(folders);
        ArgumentNullException.ThrowIfNull(logger);

        _folders = folders;
        _displayNames = displayNames;
        _labels = labels;
        _githubLinks = githubLinks;
        _registry = registry;
        _tombstones = tombstones;
        _logger = logger;
    }

    public async Task<SessionDeletionResult> DeleteAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        cancellationToken.ThrowIfCancellationRequested();

        var folderPath = _folders.GetSessionFolderPath(sessionId);

        if (!Directory.Exists(folderPath))
        {
            // Treat "already gone" as success so the UI can drop the card.
            // Still wipe sidecar state so we don't leave orphans behind.
            await ClearSidecarStateAsync(sessionId, cancellationToken).ConfigureAwait(false);
            return SessionDeletionResult.Ok(folderPath);
        }

        try
        {
            // Best-effort: clear file attributes (read-only, hidden) so the
            // recursive delete doesn't fail on a single locked-down file.
            ResetAttributes(folderPath);
            Directory.Delete(folderPath, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
            // Race: another process removed it between Exists and Delete.
            await ClearSidecarStateAsync(sessionId, cancellationToken).ConfigureAwait(false);
            return SessionDeletionResult.Ok(folderPath);
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Could not delete session folder {Folder}.", folderPath);
            return SessionDeletionResult.Failed(
                folderPath,
                $"Could not delete the session folder. It may still be in use by Copilot CLI. ({ex.Message})");
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "Permission denied deleting session folder {Folder}.", folderPath);
            return SessionDeletionResult.Failed(
                folderPath,
                $"Permission denied. Close the Copilot CLI windows for this session and try again. ({ex.Message})");
        }

        await ClearSidecarStateAsync(sessionId, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Hard-deleted session {SessionId} at {Folder}.", sessionId, folderPath);
        return SessionDeletionResult.Ok(folderPath);
    }

    private async Task ClearSidecarStateAsync(string sessionId, CancellationToken cancellationToken)
    {
        // Each side-car cleanup is best-effort — a missing override or a
        // transient I/O blip on one of them must not block the others.
        if (_displayNames is not null)
        {
            try
            {
                await _displayNames.RemoveAsync(sessionId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not clear display-name override for {Id}.", sessionId);
            }
        }

        if (_labels is not null)
        {
            try
            {
                await _labels.RemoveAsync(sessionId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not clear label for {Id}.", sessionId);
            }
        }

        if (_githubLinks is not null)
        {
            try
            {
                await _githubLinks.ClearAsync(sessionId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not clear GitHub link overrides for {Id}.", sessionId);
            }
        }

        try
        {
            _registry?.Unregister(sessionId);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not unregister running PID for {Id}.", sessionId);
        }

        // Tombstone the id LAST so the discovery service won't resurrect
        // the card from the dangling Copilot CLI session-store.db row
        // (#125). We honor ADR-002 and never touch the CLI's DB ourselves;
        // the tombstone is csm-side only.
        if (_tombstones is not null)
        {
            try
            {
                await _tombstones.RecordAsync(sessionId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Could not write tombstone for {Id}; the session may reappear on the next rescan.",
                    sessionId);
            }
        }
    }

    private static void ResetAttributes(string folder)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            {
                try
                {
                    var attrs = File.GetAttributes(file);
                    if ((attrs & (FileAttributes.ReadOnly | FileAttributes.Hidden)) != 0)
                    {
                        File.SetAttributes(file, FileAttributes.Normal);
                    }
                }
                catch
                {
                    // Best-effort; the recursive delete will surface any real failure.
                }
            }
        }
        catch
        {
            // EnumerateFiles can race; fall through to Directory.Delete.
        }
    }
}
