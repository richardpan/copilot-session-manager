namespace CopilotSessionManager.Core.Onboarding;

/// <summary>
/// Outcome of a single prerequisite check. <see cref="Warning"/> means the
/// app will work but with degraded behaviour (e.g. PowerShell 5.1 instead of
/// 7+). <see cref="Failed"/> means the user should install the missing tool.
/// </summary>
public enum PrerequisiteStatus
{
    Ok = 0,
    Warning = 1,
    Failed = 2,
}

/// <summary>
/// Result of a single prerequisite check.
/// </summary>
/// <param name="Name">Short display label, e.g. "PowerShell 7+".</param>
/// <param name="Status">Pass/warn/fail classification.</param>
/// <param name="Detail">One-line human-readable explanation rendered under
/// the name in the UI. May include a version string or path.</param>
/// <param name="InstallUrl">Optional link the UI exposes as "Install" /
/// "Learn more". Null when no remediation link applies.</param>
public sealed record PrerequisiteResult(
    string Name,
    PrerequisiteStatus Status,
    string Detail,
    string? InstallUrl);
