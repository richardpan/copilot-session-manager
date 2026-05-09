namespace CopilotSessionManager.Core.Settings;

/// <summary>
/// User-level app settings persisted to disk between launches. Designed to
/// grow over time — every property MUST have a sane default and round-trip
/// safely through JSON serialisation.
/// </summary>
public sealed class AppSettings
{
    /// <summary>
    /// True once the user has finished (or skipped) the first-run onboarding
    /// flow. Drives whether <c>OnboardingWindow</c> is shown on startup.
    /// </summary>
    public bool OnboardingCompleted { get; set; }

    /// <summary>Returns a fresh instance with all defaults.</summary>
    public static AppSettings Defaults() => new();
}
