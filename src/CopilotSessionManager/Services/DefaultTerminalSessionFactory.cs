using System;
using CopilotSessionManager.Core.Models;
using CopilotSessionManager.Terminal.Hosting;
using CopilotSessionManager.ViewModels.Terminal;

namespace CopilotSessionManager.Services;

/// <summary>
/// Production <see cref="ITerminalSessionFactory"/> used by the WPF host.
/// Spawns <c>pwsh.exe -NoLogo -NoExit -Command "copilot --resume '&lt;id&gt;'"</c>
/// inside the session's working directory via
/// <see cref="TerminalSession.Start(string, int, int, ITerminalDispatcher, string?)"/>
/// so opening a session in an embedded tab automatically attaches the
/// Copilot CLI rather than dropping the user at a bare pwsh prompt.
/// V1.5 update of the V1.4 Phase 6B factory (#159).
/// </summary>
public sealed class DefaultTerminalSessionFactory : ITerminalSessionFactory
{
    /// <summary>
    /// V1.5 — command-line used by <see cref="CreateNewCopilotSession"/>
    /// to mint a brand-new Copilot session inside an embedded tab.
    /// <c>-NoExit</c> keeps the shell up after <c>copilot</c> exits so
    /// the user can inspect output / re-run; mirrors what the V1.3
    /// external launcher used.
    /// </summary>
    private const string NewCopilotCommandLine = "pwsh.exe -NoLogo -NoExit -Command copilot";

    private readonly ITerminalDispatcher _dispatcher;

    public DefaultTerminalSessionFactory(ITerminalDispatcher dispatcher)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    /// <inheritdoc />
    public TerminalSession Create(Session session, int rows, int cols)
    {
        ArgumentNullException.ThrowIfNull(session);
        var cwd = string.IsNullOrWhiteSpace(session.Cwd) ? null : session.Cwd;
        var commandLine = BuildResumeCommandLine(session.Id);
        return TerminalSession.Start(commandLine, rows, cols, _dispatcher, cwd);
    }

    /// <inheritdoc />
    public TerminalSession CreateNewCopilotSession(int rows, int cols)
    {
        // Match PowerShellSessionLauncher.LaunchNewAsync: anchor cwd to
        // the user profile so the new session's directory mirrors what
        // V1.3 external launches recorded.
        var cwd = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(cwd))
        {
            cwd = null;
        }
        return TerminalSession.Start(NewCopilotCommandLine, rows, cols, _dispatcher, cwd);
    }

    /// <summary>
    /// Build the pwsh command-line used to attach Copilot to an existing
    /// session inside an embedded ConPTY tab. Mirrors the quoting that
    /// <c>PowerShellSessionLauncher.LaunchAsync</c> applies for the
    /// external launcher: PowerShell-style single-quote escaping
    /// (double the embedded quote). Windows <c>CreateProcess</c> parses
    /// the double-quoted region as a single <c>-Command</c> argument,
    /// which pwsh then passes intact to the Copilot CLI.
    /// </summary>
    public static string BuildResumeCommandLine(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var safeId = sessionId.Replace("'", "''", StringComparison.Ordinal);
        return $"pwsh.exe -NoLogo -NoExit -Command \"copilot --resume '{safeId}'\"";
    }
}
