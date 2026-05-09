using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using CopilotSessionManager.Core.Models;
using CopilotSessionManager.ViewModels;

namespace CopilotSessionManager.Services.Tray;

/// <summary>
/// Glue between the live <see cref="SessionCardViewModel"/> collection and
/// the OS tray icon. Owns:
/// <list type="bullet">
///   <item>Bookkeeping of the "awaiting input" count, recomputed on every
///   collection mutation and every per-card status change, and pushed onto
///   the tray's tooltip.</item>
///   <item>Forwarding of tray-driven user gestures (activate, open, quit)
///   onto plain callbacks the WPF host wires to its window/lifecycle.</item>
/// </list>
/// Decoupling the coordinator from <c>App.xaml.cs</c> lets the wiring be
/// unit-tested with an in-memory <see cref="ObservableCollection{T}"/> and
/// a fake <see cref="ITrayIconService"/>.
/// </summary>
public sealed class TrayCoordinator : IDisposable
{
    private readonly ITrayIconService _tray;
    private readonly ObservableCollection<SessionCardViewModel> _sessions;
    private readonly Action _onActivate;
    private readonly Action _onQuit;
    private bool _disposed;

    public TrayCoordinator(
        ITrayIconService tray,
        ObservableCollection<SessionCardViewModel> sessions,
        Action onActivate,
        Action onQuit)
    {
        ArgumentNullException.ThrowIfNull(tray);
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(onActivate);
        ArgumentNullException.ThrowIfNull(onQuit);

        _tray = tray;
        _sessions = sessions;
        _onActivate = onActivate;
        _onQuit = onQuit;

        _tray.ActivateRequested += OnActivateRequested;
        _tray.OpenRequested += OnOpenRequested;
        _tray.QuitRequested += OnQuitRequested;

        foreach (var card in _sessions)
        {
            card.PropertyChanged += OnCardPropertyChanged;
        }
        _sessions.CollectionChanged += OnSessionsChanged;

        Refresh();
    }

    /// <summary>The current count of sessions in <see cref="SessionStatus.AwaitingInput"/>.</summary>
    public int AwaitingInputCount { get; private set; }

    /// <summary>Recomputes the count and updates the tray tooltip. Called automatically on changes.</summary>
    public void Refresh()
    {
        if (_disposed)
        {
            return;
        }

        var count = _sessions.Count(c => c.Status == SessionStatus.AwaitingInput);
        AwaitingInputCount = count;
        _tray.UpdateAwaitingInputCount(count);
    }

    private void OnSessionsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (SessionCardViewModel card in e.OldItems)
            {
                card.PropertyChanged -= OnCardPropertyChanged;
            }
        }
        if (e.NewItems is not null)
        {
            foreach (SessionCardViewModel card in e.NewItems)
            {
                card.PropertyChanged += OnCardPropertyChanged;
            }
        }
        // Reset (e.g. Clear) wipes OldItems too — re-attach to anything that
        // remains and recompute either way.
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            foreach (var card in _sessions)
            {
                card.PropertyChanged -= OnCardPropertyChanged;
                card.PropertyChanged += OnCardPropertyChanged;
            }
        }
        Refresh();
    }

    private void OnCardPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SessionCardViewModel.Status)
            || string.IsNullOrEmpty(e.PropertyName))
        {
            Refresh();
        }
    }

    private void OnActivateRequested(object? sender, EventArgs e) => _onActivate();
    private void OnOpenRequested(object? sender, EventArgs e) => _onActivate();
    private void OnQuitRequested(object? sender, EventArgs e) => _onQuit();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        _tray.ActivateRequested -= OnActivateRequested;
        _tray.OpenRequested -= OnOpenRequested;
        _tray.QuitRequested -= OnQuitRequested;
        _sessions.CollectionChanged -= OnSessionsChanged;
        foreach (var card in _sessions)
        {
            card.PropertyChanged -= OnCardPropertyChanged;
        }
    }
}
