using System;

namespace CopilotSessionManager.Terminal.Hosting;

/// <summary>
/// Tiny abstraction that lets <see cref="TerminalSession"/> marshal work
/// from its background reader task onto the thread that owns the
/// <see cref="CopilotSessionManager.Terminal.ScreenBuffer"/> — usually the
/// WPF dispatcher thread.
/// </summary>
/// <remarks>
/// The hosting library deliberately depends only on the <c>Terminal</c>
/// (parser + buffer) and <c>Native</c> (ConPTY) projects, not on WPF, so
/// the dispatcher is injected as an interface. The main application wires
/// up a WPF <see cref="T:System.Windows.Threading.Dispatcher"/>-backed
/// implementation; tests inject a synchronous one.
/// </remarks>
public interface ITerminalDispatcher
{
    /// <summary>
    /// Schedule <paramref name="action"/> to run on the UI thread. The
    /// implementation may run the action synchronously if the caller is
    /// already on the UI thread; reader-task callers will always be on a
    /// background thread, so this typically posts to a message queue.
    /// </summary>
    void Post(Action action);
}
