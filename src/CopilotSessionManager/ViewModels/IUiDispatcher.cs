using System;

namespace CopilotSessionManager.ViewModels;

/// <summary>
/// Tiny abstraction so view models can post work back to the UI thread without
/// depending on <c>System.Windows.Threading.Dispatcher</c> directly. The WPF
/// host registers a real implementation; tests use a synchronous one.
/// </summary>
public interface IUiDispatcher
{
    /// <summary>Runs <paramref name="action"/> on the UI thread.</summary>
    void Post(Action action);
}
