namespace CopilotSessionManager.ViewModels;

/// <summary>
/// Where a single <see cref="IssueLinkViewModel"/> came from. Drives badge
/// affordances such as whether the user can manually remove the link
/// (parsed-from-README links can't — they come back on the next scan).
/// </summary>
public enum IssueLinkOrigin
{
    /// <summary>User explicitly added this issue via the "+ Issue" dialog.</summary>
    Manual = 0,

    /// <summary>Discovered by scanning the session's auto-generated README.</summary>
    ParsedFromReadme = 1,
}
