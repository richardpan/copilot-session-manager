using System;
using CopilotSessionManager.Core.Models;
using CopilotSessionManager.Terminal.Hosting;
using CopilotSessionManager.ViewModels.Terminal;

namespace CopilotSessionManager.Services;

/// <summary>
/// Production <see cref="ITerminalSessionFactory"/> used by the WPF host.
/// Spawns <c>pwsh.exe -NoLogo</c> inside the session's working directory
/// via <see cref="TerminalSession.Start(string, int, int, ITerminalDispatcher, string?)"/>
/// and hands ownership back to the caller. Phase 6B (#159).
/// </summary>
public sealed class DefaultTerminalSessionFactory : ITerminalSessionFactory
{
    private const string DefaultShellCommandLine = "pwsh.exe -NoLogo";

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
        return TerminalSession.Start(DefaultShellCommandLine, rows, cols, _dispatcher, cwd);
    }
}
