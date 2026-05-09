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
    /// when the backing file does not exist or fails to deserialise.
    /// </summary>
    Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists <paramref name="settings"/>, overwriting whatever was
    /// previously stored.
    /// </summary>
    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default);
}
