using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CopilotSessionManager.Core.Models;
using CopilotSessionManager.Terminal.Hosting;

namespace CopilotSessionManager.ViewModels.Terminal;

/// <summary>
/// View-model for the tabbed terminal surface introduced in Phase 6A of
/// issue #159. Owns the <see cref="ObservableCollection{T}"/> of
/// <see cref="TerminalTabViewModel"/>, the active-tab selection, and
/// the find-or-create logic that Phase 6B will route
/// <c>SessionCardViewModel.OpenCommand</c> into.
/// </summary>
/// <remarks>
/// <para>
/// View-models depend only on <c>CopilotSessionManager.Terminal.Hosting</c>;
/// the host injects an <see cref="ITerminalSessionFactory"/> so tests
/// can swap in a fake that returns sessions wrapped around in-memory
/// pipes.
/// </para>
/// <para>
/// Default tab dimensions (<c>30 × 100</c>) match the Phase 3E debug
/// terminal window so visual output is identical until #176
/// (auto-resize) lands and the view starts driving real dimensions
/// from the hosting control's pixel size.
/// </para>
/// </remarks>
public sealed partial class TerminalTabsViewModel : ObservableObject, IDisposable
{
    private const int DefaultRows = 30;
    private const int DefaultCols = 100;

    private readonly ITerminalSessionFactory _sessionFactory;

    /// <summary>Construct a tabs view-model around the supplied session factory.</summary>
    public TerminalTabsViewModel(ITerminalSessionFactory sessionFactory)
    {
        _sessionFactory = sessionFactory ?? throw new ArgumentNullException(nameof(sessionFactory));
    }

    /// <summary>The tabs displayed in the strip, in left-to-right order.</summary>
    public ObservableCollection<TerminalTabViewModel> Tabs { get; } = new();

    /// <summary>The active tab (bound to <c>TabControl.SelectedItem</c>) or null if no tabs are open.</summary>
    [ObservableProperty]
    private TerminalTabViewModel? _activeTab;

    /// <summary>True when there are no tabs open; the view collapses the strip when this is true.</summary>
    public bool IsEmpty => Tabs.Count == 0;

    /// <summary>
    /// Find an existing tab for <paramref name="session"/> and activate
    /// it; or create a new one (using
    /// <paramref name="displayName"/> + <paramref name="tierAccent"/>),
    /// append it to the strip, and activate it. Returns the activated
    /// tab.
    /// </summary>
    /// <param name="session">Dashboard session this tab represents.</param>
    /// <param name="displayName">
    /// Tab header text. Phase 6B's caller derives this from the
    /// session-card view-model so per-card rename overrides are honoured.
    /// </param>
    /// <param name="tierAccent">
    /// Accent brush painted into the tab-header stripe so the tab
    /// visually matches the dashboard card's tier badge.
    /// </param>
    public TerminalTabViewModel OpenOrActivate(Session session, string displayName, Brush tierAccent)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(displayName);
        ArgumentNullException.ThrowIfNull(tierAccent);

        var existing = Tabs.FirstOrDefault(t => string.Equals(t.SessionId, session.Id, StringComparison.Ordinal));
        if (existing is not null)
        {
            // Refresh the header projection so renames / tier changes
            // surface on next activation without reopening the tab.
            existing.DisplayName = displayName;
            existing.TierAccent = tierAccent;
            ActiveTab = existing;
            return existing;
        }

        var terminalSession = _sessionFactory.Create(session, DefaultRows, DefaultCols);
        var tab = new TerminalTabViewModel(session.Id, displayName, tierAccent, terminalSession);
        Tabs.Add(tab);
        ActiveTab = tab;
        OnPropertyChanged(nameof(IsEmpty));
        return tab;
    }

    /// <summary>
    /// Close <paramref name="tab"/>: remove it from <see cref="Tabs"/>,
    /// dispose it, and pick a sensible neighbour as the new active tab.
    /// No-op when the tab is not in the strip.
    /// </summary>
    public void Close(TerminalTabViewModel tab)
    {
        ArgumentNullException.ThrowIfNull(tab);

        var index = Tabs.IndexOf(tab);
        if (index < 0)
        {
            return;
        }

        var wasActive = ReferenceEquals(ActiveTab, tab);
        Tabs.RemoveAt(index);
        try
        {
            tab.Dispose();
        }
        catch
        {
            // Surfaced via logs elsewhere; do not let teardown bring down the strip.
        }

        if (wasActive)
        {
            // Activate the tab that visually slid into this position
            // (the new tab at the same index, or the rightmost if we removed the last one).
            if (Tabs.Count == 0)
            {
                ActiveTab = null;
            }
            else if (index < Tabs.Count)
            {
                ActiveTab = Tabs[index];
            }
            else
            {
                ActiveTab = Tabs[^1];
            }
        }

        OnPropertyChanged(nameof(IsEmpty));
    }

    /// <summary>Close every tab. Used during host shutdown.</summary>
    public void CloseAll()
    {
        // Snapshot because Close mutates the collection.
        foreach (var tab in Tabs.ToArray())
        {
            Close(tab);
        }
    }

    /// <summary>
    /// Phase 6C (#159): close glyph / middle-click / external callers
    /// share this command. Tolerates a null parameter (no-op) so the
    /// XAML <c>CommandParameter</c> binding can fail gracefully during
    /// teardown without throwing.
    /// </summary>
    [RelayCommand]
    private void CloseTab(TerminalTabViewModel? tab)
    {
        if (tab is null)
        {
            return;
        }
        Close(tab);
    }

    /// <summary>
    /// Phase 6C (#159): Ctrl+Tab cycles forward through the open tabs
    /// (wraps from the last tab back to the first). No-op when fewer
    /// than two tabs are open so the keybinding doesn't spuriously
    /// flicker the active tab.
    /// </summary>
    [RelayCommand]
    private void CycleNext()
    {
        if (Tabs.Count < 2)
        {
            return;
        }
        var index = ActiveTab is null ? -1 : Tabs.IndexOf(ActiveTab);
        ActiveTab = Tabs[(index + 1 + Tabs.Count) % Tabs.Count];
    }

    /// <summary>
    /// Phase 6C (#159): Ctrl+Shift+Tab cycles backwards through the
    /// open tabs (wraps from the first tab to the last). No-op when
    /// fewer than two tabs are open.
    /// </summary>
    [RelayCommand]
    private void CyclePrevious()
    {
        if (Tabs.Count < 2)
        {
            return;
        }
        var index = ActiveTab is null ? 0 : Tabs.IndexOf(ActiveTab);
        ActiveTab = Tabs[(index - 1 + Tabs.Count) % Tabs.Count];
    }

    /// <inheritdoc />
    public void Dispose() => CloseAll();

    partial void OnActiveTabChanged(TerminalTabViewModel? oldValue, TerminalTabViewModel? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.IsActive = false;
        }
        if (newValue is not null)
        {
            newValue.IsActive = true;
        }
    }
}
