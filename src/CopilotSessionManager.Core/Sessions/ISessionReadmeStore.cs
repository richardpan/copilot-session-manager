using System;
using System.Threading;
using System.Threading.Tasks;

namespace CopilotSessionManager.Core.Sessions;

/// <summary>
/// File-backed store for <c>SESSION-README.md</c>. Implementations are
/// responsible for atomic writes and for preserving user-editable blocks
/// (delimited by <c>USER:BEGIN</c> / <c>USER:END</c> HTML comments) across
/// regenerations.
/// </summary>
public interface ISessionReadmeStore
{
    /// <summary>The path used to store the README for <paramref name="sessionId"/>.</summary>
    string GetReadmePath(string sessionId);

    /// <summary>True if a README file exists on disk for <paramref name="sessionId"/>.</summary>
    bool Exists(string sessionId);

    /// <summary>
    /// Reads the current contents from disk, or <c>null</c> if no README has
    /// been written yet.
    /// </summary>
    Task<string?> ReadAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes <paramref name="freshlyRendered"/> for <paramref name="sessionId"/>,
    /// splicing in any user-editable blocks from the existing file (if present).
    /// Atomic: writes to a temp file first then renames into place. Returns the
    /// final on-disk content.
    /// </summary>
    Task<string> WriteAsync(
        string sessionId,
        string freshlyRendered,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Raised after <see cref="WriteAsync"/> finishes successfully. May be
    /// invoked from a background thread.
    /// </summary>
    event EventHandler<SessionReadmeChangedEventArgs>? ReadmeChanged;
}

/// <summary>Payload for <see cref="ISessionReadmeStore.ReadmeChanged"/>.</summary>
public sealed class SessionReadmeChangedEventArgs : EventArgs
{
    public SessionReadmeChangedEventArgs(string sessionId, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        SessionId = sessionId;
        Path = path;
    }

    public string SessionId { get; }
    public string Path { get; }
}
