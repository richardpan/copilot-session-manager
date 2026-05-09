namespace CopilotSessionManager.Core.Onboarding;

/// <summary>
/// Runs all five first-run prerequisite checks and returns the results in a
/// stable order so the UI can render them as a deterministic checklist.
/// </summary>
public interface IPrerequisiteChecker
{
    /// <summary>
    /// Runs every check sequentially. The returned list is in display order:
    /// PowerShell 7+, Copilot CLI, gh CLI, gh authenticated, ~/.copilot
    /// access. Always returns a result per check (never throws for a missing
    /// CLI — a failed probe surfaces as <see cref="PrerequisiteStatus.Failed"/>).
    /// </summary>
    Task<IReadOnlyList<PrerequisiteResult>> CheckAllAsync(CancellationToken cancellationToken = default);
}
