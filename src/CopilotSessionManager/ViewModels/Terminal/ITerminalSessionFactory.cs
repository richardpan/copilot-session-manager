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

    /// <summary>
    /// V1.5 — spawn a brand-new Copilot session inside an embedded
    /// terminal. Used by the dashboard's "New session" affordance when
    /// the embedded route is selected (the default). Implementations
    /// run <c>copilot</c> with no <c>--resume</c> so the CLI mints a
    /// fresh session id; the dashboard's discovery watcher then picks
    /// the new session up and surfaces it as a card.
    /// </summary>
    /// <param name="rows">Initial row count for the pseudo-console (&gt; 0).</param>
    /// <param name="cols">Initial column count for the pseudo-console (&gt; 0).</param>
    TerminalSession CreateNewCopilotSession(int rows, int cols);
}
