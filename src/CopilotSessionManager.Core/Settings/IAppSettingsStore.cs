namespace CopilotSessionManager.Core.Settings;

/// <summary>
/// Persists user-level <see cref="AppSettings"/> to disk. Implementations
/// must tolerate a missing or corrupt backing file (returning defaults) and
/// must serialise writes so concurrent saves don't interleave.
/// </summary>
public interface IAppSettingsStore
{
    /// <summary>
    /// Loads the current settings. Returns <see cref="AppSettings.Defaults"/>
    /// when the backing file does not exist or fails to deserialise. May
    /// transparently run schema migrations if the on-disk version is older
    /// than <see cref="AppSettings.CurrentSchemaVersion"/>.
    /// </summary>
    Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists <paramref name="settings"/>, overwriting whatever was
    /// previously stored.
    /// </summary>
    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default);

    /// <summary>
    /// Wipes the backing file so the next <see cref="LoadAsync"/> returns
    /// <see cref="AppSettings.Defaults"/>. Any previous content is preserved
    /// in a one-shot <c>.reset.bak</c> sibling so the user can recover by
    /// hand if they reset by accident. Idempotent — does nothing if the
    /// backing file does not exist.
    /// </summary>
    Task ResetAsync(CancellationToken cancellationToken = default);
}
