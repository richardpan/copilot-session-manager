using System;
using System.ComponentModel;
using System.Linq;
using CopilotSessionManager.ViewModels;
using CopilotSessionManager.ViewModels.Terminal;

namespace CopilotSessionManager.Services;

/// <summary>
/// Phase 6D (#159): keeps the dashboard's
/// <see cref="SessionsViewModel.SelectedCard"/> and the embedded tab
/// strip's <see cref="TerminalTabsViewModel.ActiveTab"/> in sync so the
/// user can drive selection from either surface.
/// </summary>
/// <remarks>
/// <para>
/// Card -> tab: when the user selects a card that has an embedded tab
/// open, the tab is activated. Cards without an open tab leave the
/// strip alone (we don't auto-open).
/// </para>
/// <para>
/// Tab -> card: when the user clicks a tab header, the matching card
/// is selected in the dashboard. Tabs whose session has been removed
/// from the dashboard (mid-flight) are left without a paired card.
/// </para>
/// <para>
/// A re-entrancy flag short-circuits the second leg so the two
/// PropertyChanged subscriptions never form a feedback loop.
/// </para>
/// </remarks>
public sealed class DashboardTerminalTabsCoordinator : IDisposable
{
    private readonly SessionsViewModel _sessions;
    private readonly TerminalTabsViewModel _tabs;
    private bool _syncing;
    private bool _disposed;

    /// <summary>Subscribe to PropertyChanged on both view-models.</summary>
    public DashboardTerminalTabsCoordinator(SessionsViewModel sessions, TerminalTabsViewModel tabs)
    {
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _tabs = tabs ?? throw new ArgumentNullException(nameof(tabs));

        _sessions.PropertyChanged += OnSessionsPropertyChanged;
        _tabs.PropertyChanged += OnTabsPropertyChanged;
    }

    private void OnSessionsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_syncing || e.PropertyName != nameof(SessionsViewModel.SelectedCard))
        {
            return;
        }

        var card = _sessions.SelectedCard;
        if (card is null)
        {
            return;
        }

        var matchingTab = _tabs.Tabs.FirstOrDefault(t => string.Equals(t.SessionId, card.Id, StringComparison.Ordinal));
        if (matchingTab is null || ReferenceEquals(_tabs.ActiveTab, matchingTab))
        {
            return;
        }

        _syncing = true;
        try
        {
            _tabs.ActiveTab = matchingTab;
        }
        finally
        {
            _syncing = false;
        }
    }

    private void OnTabsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_syncing || e.PropertyName != nameof(TerminalTabsViewModel.ActiveTab))
        {
            return;
        }

        var tab = _tabs.ActiveTab;
        if (tab is null)
        {
            return;
        }

        var matchingCard = _sessions.Sessions.FirstOrDefault(c => string.Equals(c.Id, tab.SessionId, StringComparison.Ordinal));
        if (matchingCard is null || ReferenceEquals(_sessions.SelectedCard, matchingCard))
        {
            return;
        }

        _syncing = true;
        try
        {
            _sessions.SelectedCard = matchingCard;
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _sessions.PropertyChanged -= OnSessionsPropertyChanged;
        _tabs.PropertyChanged -= OnTabsPropertyChanged;
    }
}
