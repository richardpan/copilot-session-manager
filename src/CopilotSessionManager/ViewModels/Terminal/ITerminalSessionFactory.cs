using System;
using CopilotSessionManager.Core.Models;
using CopilotSessionManager.Terminal.Hosting;

namespace CopilotSessionManager.ViewModels.Terminal;

/// <summary>
/// Factory that produces a fresh <see cref="TerminalSession"/> for a
/// given dashboard <see cref="Session"/>. Pulled behind an interface so
/// the tab view-models can be unit-tested without spawning a real
/// pseudo-console. Phase 6A of issue #159.
/// </summary>
public interface ITerminalSessionFactory
{
    /// <summary>
    /// Create a session bound to <paramref name="session"/>. Implementations
    /// own the lifetime decisions (which shell, working directory, initial
    /// dimensions); callers only need to dispose the returned session.
    /// </summary>
    /// <param name="session">The dashboard session this tab represents.</param>
    /// <param name="rows">Initial row count for the pseudo-console (&gt; 0).</param>
    /// <param name="cols">Initial column count for the pseudo-console (&gt; 0).</param>
    TerminalSession Create(Session session, int rows, int cols);
}
