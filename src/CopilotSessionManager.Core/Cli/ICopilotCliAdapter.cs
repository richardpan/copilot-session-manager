using CopilotSessionManager.Core.Models;

namespace CopilotSessionManager.Core.Cli;

/// <summary>
/// Versioned read-only adapter over Copilot CLI's on-disk artifacts. One
/// implementation exists per supported CLI major/minor range. See ADR-0003.
/// </summary>
public interface ICopilotCliAdapter
{
    /// <summary>Inclusive minimum CLI version this adapter understands.</summary>
    CopilotVersion MinSupported { get; }

    /// <summary>Inclusive maximum CLI version this adapter understands.</summary>
    CopilotVersion MaxSupported { get; }

    /// <summary>True if this adapter claims compatibility with <paramref name="version"/>.</summary>
    bool Supports(CopilotVersion version);

    /// <summary>
    /// Read the <c>copilotVersion</c> recorded in the first <c>session.start</c>
    /// event of <paramref name="eventsJsonl"/>. Returns <c>null</c> if no such
    /// event exists or the version is unparseable.
    /// </summary>
    Task<CopilotVersion?> ReadCopilotVersionAsync(
        Stream eventsJsonl,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Stream events from <paramref name="eventsJsonl"/> as parsed
    /// <see cref="SessionEvent"/> records. Malformed lines are skipped (and
    /// logged by the implementation).
    /// </summary>
    IAsyncEnumerable<SessionEvent> ParseEventsAsync(
        Stream eventsJsonl,
        CancellationToken cancellationToken = default);

    /// <summary>Parse the <c>workspace.yaml</c> contents.</summary>
    WorkspaceManifest ParseWorkspace(string yaml);
}
