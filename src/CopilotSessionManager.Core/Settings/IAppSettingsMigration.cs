namespace CopilotSessionManager.Core.Settings;

/// <summary>
/// One step in the upgrade chain for <see cref="AppSettings"/>. A migration
/// runs when the loaded file's schema version equals
/// <see cref="FromVersion"/>. After <see cref="Apply"/> returns, the file is
/// stamped with <see cref="FromVersion"/> + 1 and the next migration in the
/// ordered set runs (if any).
/// </summary>
/// <remarks>
/// Migrations operate on the already-deserialised <see cref="AppSettings"/>
/// instance. They MUST be deterministic, MUST NOT throw on data they don't
/// recognise, and SHOULD avoid I/O. Any thrown exception aborts the load
/// and triggers a rollback to the pre-migration backup.
/// </remarks>
public interface IAppSettingsMigration
{
    /// <summary>The version this migration upgrades <em>from</em>. The post-state is <c>FromVersion + 1</c>.</summary>
    int FromVersion { get; }

    /// <summary>Applies the migration in place.</summary>
    void Apply(AppSettings settings);
}
