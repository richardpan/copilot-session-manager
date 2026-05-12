using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CopilotSessionManager.Core.Sessions;

/// <summary>
/// Tombstone registry for sessions hard-deleted via
/// <see cref="ISessionDeletionService"/>.
/// </summary>
/// <remarks>
/// <para>
/// Required because csm honors ADR-002 and never writes to Copilot CLI's
/// <c>session-store.db</c>. After the on-disk
/// <c>~/.copilot/session-state/&lt;id&gt;/</c> folder is removed, the DB row
/// is intentionally left behind. Without a tombstone the next discovery
/// rescan would see the dangling row, fall back to its "DB-only" code
/// path, and resurrect the session card a few hundred milliseconds after
/// the user deleted it.
/// </para>
/// <para>
/// Persisted to <c>%LOCALAPPDATA%\CopilotSessionManager\deleted-sessions.json</c>
/// so a tombstone survives an app restart. A tombstone is automatically
/// pruned the moment the on-disk folder is observed again — that means a
/// session id that gets re-used (e.g. the user manually re-imports
/// content under the same id, or Copilot CLI reaps the row and reissues
/// it later) will start showing up again.
/// </para>
/// </remarks>
public interface IDeletedSessionRegistry
{
    /// <summary>
    /// Records <paramref name="sessionId"/> as hard-deleted. Idempotent.
    /// </summary>
    Task RecordAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes any tombstone for <paramref name="sessionId"/>. No-op when
    /// no tombstone exists. Used by the discovery service to self-heal
    /// when the on-disk folder reappears.
    /// </summary>
    Task ForgetAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>True when <paramref name="sessionId"/> has been tombstoned.</summary>
    Task<bool> IsDeletedAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>Snapshot of all currently-tombstoned ids.</summary>
    Task<IReadOnlySet<string>> GetAllAsync(CancellationToken cancellationToken = default);
}
