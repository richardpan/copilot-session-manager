using CopilotSessionManager.Core.Configuration;

namespace CopilotSessionManager.Core.Sessions;

/// <summary>
/// Indirection over filesystem paths owned by the Copilot CLI. Allows tests
/// to point at a temp directory instead of the user's real <c>~/.copilot/</c>.
/// </summary>
public interface ICopilotPaths
{
    /// <summary>Path to <c>~/.copilot/session-store.db</c>.</summary>
    string SessionStoreDatabasePath { get; }

    /// <summary>Path to <c>~/.copilot/session-state/</c>.</summary>
    string SessionStateDirectory { get; }
}

/// <summary>Default <see cref="ICopilotPaths"/> backed by <see cref="AppPaths"/>.</summary>
public sealed class DefaultCopilotPaths : ICopilotPaths
{
    public string SessionStoreDatabasePath => AppPaths.CopilotSessionStoreDatabasePath;
    public string SessionStateDirectory => AppPaths.CopilotSessionStateDirectory;
}
