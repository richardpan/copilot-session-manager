using System;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CopilotSessionManager.Core.Models;
using CopilotSessionManager.Terminal.Hosting;

namespace CopilotSessionManager.ViewModels.Terminal;

/// <summary>
/// View-model for a single tab in the embedded terminal tab strip.
/// Owns the <see cref="TerminalSession"/> for one Copilot session and
/// surfaces the bits the tab header / content template bind to.
/// Phase 6A of issue #159.
/// </summary>
/// <remarks>
/// Tabs are uniquely identified by <see cref="SessionId"/>; the tabs
/// view-model uses that key to find-or-create on
/// <c>OpenOrActivate(card)</c> in Phase 6B.
/// </remarks>
public sealed partial class TerminalTabViewModel : ObservableObject, IDisposable
{
    private bool _disposed;

    /// <summary>Create a tab around an already-started session.</summary>
    /// <param name="sessionId">Dashboard session id this tab represents.</param>
    /// <param name="displayName">Tab header text (truncates as the header template sees fit).</param>
    /// <param name="tierAccent">Accent brush painted into the tab-header stripe (Phase 6C); never null.</param>
    /// <param name="terminalSession">Active <see cref="TerminalSession"/> the tab owns.</param>
    public TerminalTabViewModel(string sessionId, string displayName, Brush tierAccent, TerminalSession terminalSession)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(displayName);
        ArgumentNullException.ThrowIfNull(tierAccent);
        ArgumentNullException.ThrowIfNull(terminalSession);

        SessionId = sessionId;
        DisplayName = displayName;
        TierAccent = tierAccent;
        TerminalSession = terminalSession;
    }

    /// <summary>Dashboard session id; the tab-finder uses this as a key.</summary>
    public string SessionId { get; }

    /// <summary>Text shown in the tab header.</summary>
    [ObservableProperty]
    private string _displayName;

    /// <summary>Coloured stripe painted into the tab header to match the dashboard's tier badge.</summary>
    [ObservableProperty]
    private Brush _tierAccent;

    /// <summary>True when this tab is the active one in the strip. Bound from <c>TerminalTabsViewModel.ActiveTab</c>.</summary>
    [ObservableProperty]
    private bool _isActive;

    /// <summary>The terminal session this tab owns. Disposed by <see cref="Dispose"/>.</summary>
    public TerminalSession TerminalSession { get; }

    /// <summary>True once <see cref="Dispose"/> has run; tests use this to assert teardown.</summary>
    public bool IsDisposed => _disposed;

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        try
        {
            TerminalSession.Dispose();
        }
        catch
        {
            // Disposal is best-effort; never let it tear down the whole view-model layer.
        }
    }
}
