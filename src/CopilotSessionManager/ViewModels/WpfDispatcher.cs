using System;
using System.Windows.Threading;

namespace CopilotSessionManager.ViewModels;

/// <summary>
/// WPF <see cref="Dispatcher"/>-backed implementation of <see cref="IUiDispatcher"/>.
/// </summary>
public sealed class WpfDispatcher : IUiDispatcher
{
    private readonly Dispatcher _dispatcher;

    public WpfDispatcher(Dispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        _dispatcher = dispatcher;
    }

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
