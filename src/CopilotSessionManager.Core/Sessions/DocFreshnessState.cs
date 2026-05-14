namespace CopilotSessionManager.Core.Sessions;

/// <summary>
/// V1.3 (#147): Traffic-light freshness state for a session's
/// <c>SESSION-README.md</c> / <c>SESSION-DOCS.md</c>. Drives the "Docs"
/// column badge on the sessions data-table.
/// </summary>
public enum DocFreshnessState
{
    /// <summary>Doc was updated within the last day.</summary>
    Fresh = 0,

    /// <summary>Doc is 1–7 days stale.</summary>
    Stale = 1,

    /// <summary>Doc is more than 7 days stale.</summary>
    VeryStale = 2,

    /// <summary>No <c>SESSION-README.md</c> or <c>SESSION-DOCS.md</c> exists yet.</summary>
    Missing = 3,

    /// <summary>Session is younger than the threshold (default 30 min); freshness not yet meaningful.</summary>
    NotApplicable = 4,
}
