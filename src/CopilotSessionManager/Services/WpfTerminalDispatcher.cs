using System;
using System.Windows.Threading;
using CopilotSessionManager.Terminal.Hosting;

namespace CopilotSessionManager.Services;

/// <summary>
/// WPF <see cref="Dispatcher"/>-backed implementation of
/// <see cref="ITerminalDispatcher"/>. Lives in the host project (rather
/// than in the Hosting library) so the library can stay WPF-free and
/// unit-testable.
/// </summary>
/// <remarks>
/// Promoted out of <c>Views/TerminalWindow.xaml.cs</c> in Phase 6B
/// (#159) so the embedded-tab path can resolve the dispatcher from DI
/// rather than building one per window.
/// </remarks>
public sealed class WpfTerminalDispatcher : ITerminalDispatcher
{
    private readonly Dispatcher _dispatcher;

    public WpfTerminalDispatcher(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    /// <inheritdoc />
    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (_dispatcher.CheckAccess())
        {
            action();
            return;
        }

        _dispatcher.BeginInvoke(action);
    }
}
