namespace CopilotSessionManager.Core.Sessions;

/// <summary>
/// Pure renderer that turns a <see cref="SessionReadmeContext"/> into the
/// auto-generated portion of <c>SESSION-README.md</c>. The output may include
/// empty <c>USER:BEGIN/USER:END</c> blocks for user-editable sections, but
/// preservation of any existing user content across regeneration is the
/// responsibility of <see cref="ISessionReadmeStore"/>, not the renderer.
/// </summary>
public interface ISessionReadmeRenderer
{
    /// <summary>
    /// Returns the full markdown body for a freshly rendered README, including
    /// empty placeholders for user-editable sections.
    /// </summary>
    string Render(SessionReadmeContext context);
}
