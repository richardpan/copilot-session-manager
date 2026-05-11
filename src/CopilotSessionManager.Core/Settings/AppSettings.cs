namespace CopilotSessionManager.Core.Settings;

/// <summary>
/// User-level app settings persisted to disk between launches. Designed to
/// grow over time — every property MUST have a sane default and round-trip
/// safely through JSON serialisation.
/// </summary>
public sealed class AppSettings
{
    /// <summary>
    /// Latest schema version this code base understands. Bump this whenever
    /// you add a setting that requires a non-trivial migration from the
    /// previous shape (e.g. renamed property, changed enum representation,
    /// extracted nested object). Adding a new optional property with a sane
    /// default is NOT a breaking change and does not require a bump.
    /// </summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>
    /// Schema version of this in-memory instance. New objects start at the
    /// current version. Files written before versioning was introduced (or
    /// hand-edited to drop the field) deserialise as <c>0</c>, which the
    /// migration pipeline upgrades on next load.
    /// </summary>
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>
    /// True once the user has finished (or skipped) the first-run onboarding
    /// flow. Drives whether <c>OnboardingWindow</c> is shown on startup.
    /// </summary>
    public bool OnboardingCompleted { get; set; }

    /// <summary>
    /// Minimum logging level. Accepted values: <c>"Information"</c> (default)
    /// or <c>"Debug"</c>. Anything else is treated as
    /// <c>"Information"</c>. Used by the Serilog
    /// <c>LoggingLevelSwitch</c> in <c>App.xaml.cs</c>.
    /// </summary>
    public string LogLevel { get; set; } = "Information";

    /// <summary>
    /// When <c>true</c> (default), pressing the close button on the main
    /// window hides the window into the system tray instead of exiting the
    /// process. The user can still quit explicitly via the tray context
    /// menu or <c>File &gt; Quit</c>. Flip to <c>false</c> for "always exit
    /// on close" behaviour. Additive, non-breaking — no schema bump needed.
    /// </summary>
    public bool MinimizeToTrayOnClose { get; set; } = true;

    /// <summary>
    /// When <c>true</c>, csm runs the equivalent of the toolbar
    /// <c>🧹 Clean stale locks</c> command once after the initial session
    /// scan completes on every launch, sweeping
    /// <c>~/.copilot/session-state</c> for <c>inuse.&lt;pid&gt;.lock</c>
    /// files whose owning process is no longer running. Live locks are never
    /// touched, so it is always safe to enable. Defaults to <c>false</c> so
    /// the historical opt-in cleanup behaviour is preserved. Additive,
    /// non-breaking — no schema bump needed.
    /// </summary>
    public bool AutoCleanStaleLocksOnStartup { get; set; }

    /// <summary>Returns a fresh instance with all defaults.</summary>
    public static AppSettings Defaults() => new();
}
