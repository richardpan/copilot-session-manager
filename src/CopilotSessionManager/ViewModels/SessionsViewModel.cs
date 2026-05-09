using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CopilotSessionManager.Core.Models;
using CopilotSessionManager.Core.Sessions;
using Microsoft.Extensions.Logging;

namespace CopilotSessionManager.ViewModels;

/// <summary>
/// Top-level view model behind the sessions dashboard. Owns the live
/// <see cref="ObservableCollection{T}"/> of <see cref="SessionCardViewModel"/>,
/// subscribes to <see cref="ISessionDiscoveryService.SessionsChanged"/>, and
/// applies the active-only filter.
/// </summary>
public sealed partial class SessionsViewModel : ObservableObject, IAsyncDisposable
{
    private readonly ISessionDiscoveryService _discovery;
    private readonly IUiDispatcher _dispatcher;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SessionsViewModel> _logger;

    private readonly Dictionary<string, SessionCardViewModel> _byId =
        new(StringComparer.OrdinalIgnoreCase);
    private bool _started;
    private bool _disposed;

    [ObservableProperty]
    private bool _showInactive = true;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _statusMessage = "Ready.";

    public SessionsViewModel(
        ISessionDiscoveryService discovery,
        IUiDispatcher dispatcher,
        TimeProvider timeProvider,
        ILogger<SessionsViewModel> logger)
    {
        ArgumentNullException.ThrowIfNull(discovery);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _discovery = discovery;
        _dispatcher = dispatcher;
        _timeProvider = timeProvider;
        _logger = logger;

        Sessions = new ObservableCollection<SessionCardViewModel>();
        VisibleSessions = new ObservableCollection<SessionCardViewModel>();
    }

    /// <summary>The full set of discovered sessions (unfiltered).</summary>
    public ObservableCollection<SessionCardViewModel> Sessions { get; }

    /// <summary>Sessions after the active-only filter has been applied.</summary>
    public ObservableCollection<SessionCardViewModel> VisibleSessions { get; }

    public int TotalCount => Sessions.Count;

    public int ActiveCount
    {
        get
        {
            var n = 0;
            foreach (var s in Sessions)
            {
                if (IsActive(s.Status))
                {
                    n++;
                }
            }
            return n;
        }
    }

    /// <summary>
    /// Performs the initial scan and starts the watcher. Subsequent calls
    /// return immediately.
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_started)
        {
            return;
        }
        _started = true;

        try
        {
            IsLoading = true;
            StatusMessage = "Scanning sessions…";

            _discovery.SessionsChanged += OnSessionsChanged;
            await _discovery.StartWatchingAsync(cancellationToken).ConfigureAwait(false);

            // StartWatchingAsync does an initial scan internally and updates
            // CurrentSessions; mirror it now so the UI has data immediately.
            ApplySnapshot(_discovery.CurrentSessions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Initial session scan failed.");
            StatusMessage = $"Scan failed: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            IsLoading = true;
            StatusMessage = "Rescanning…";
            var sessions = await _discovery.ScanAsync(cancellationToken).ConfigureAwait(false);
            ApplySnapshot(sessions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Rescan failed.");
            StatusMessage = $"Rescan failed: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    partial void OnShowInactiveChanged(bool value) => RebuildVisible();

    private void OnSessionsChanged(object? sender, SessionsChangedEventArgs e)
    {
        // Marshal to the UI thread so ObservableCollection mutations are safe.
        _dispatcher.Post(() => ApplySnapshot(e.Sessions));
    }

    private void ApplySnapshot(IReadOnlyList<Session> snapshot)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var inserted = false;

        foreach (var session in snapshot)
        {
            seen.Add(session.Id);

            if (_byId.TryGetValue(session.Id, out var existing))
            {
                existing.UpdateFrom(session);
            }
            else
            {
                var card = new SessionCardViewModel(session, _timeProvider);
                _byId[session.Id] = card;
                Sessions.Add(card);
                inserted = true;
            }
        }

        // Remove sessions no longer reported by discovery.
        for (var i = Sessions.Count - 1; i >= 0; i--)
        {
            var card = Sessions[i];
            if (!seen.Contains(card.Id))
            {
                Sessions.RemoveAt(i);
                _byId.Remove(card.Id);
            }
        }

        if (inserted || snapshot.Count == 0)
        {
            // Re-sort by status priority, then UpdatedAt desc.
            ResortInPlace();
        }

        RebuildVisible();

        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(ActiveCount));
        StatusMessage = $"{ActiveCount} active / {TotalCount} total at {_timeProvider.GetLocalNow():t}.";
    }

    private void ResortInPlace()
    {
        var sorted = new List<SessionCardViewModel>(Sessions);
        sorted.Sort(static (a, b) =>
        {
            var byStatus = StatusPriority(a.Status).CompareTo(StatusPriority(b.Status));
            return byStatus != 0
                ? byStatus
                : b.Model.UpdatedAt.CompareTo(a.Model.UpdatedAt);
        });

        for (var i = 0; i < sorted.Count; i++)
        {
            var current = Sessions.IndexOf(sorted[i]);
            if (current != i)
            {
                Sessions.Move(current, i);
            }
        }
    }

    private void RebuildVisible()
    {
        VisibleSessions.Clear();
        foreach (var card in Sessions)
        {
            if (ShowInactive || IsActive(card.Status))
            {
                VisibleSessions.Add(card);
            }
        }
    }

    private static bool IsActive(SessionStatus status) =>
        status is SessionStatus.Working
            or SessionStatus.AwaitingApproval
            or SessionStatus.AwaitingInput
            or SessionStatus.Idle;

    private static int StatusPriority(SessionStatus status) => status switch
    {
        SessionStatus.AwaitingApproval => 0,
        SessionStatus.AwaitingInput => 1,
        SessionStatus.Working => 2,
        SessionStatus.Idle => 3,
        SessionStatus.Orphaned => 4,
        SessionStatus.Inactive => 5,
        _ => 6,
    };

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        _discovery.SessionsChanged -= OnSessionsChanged;
        try
        {
            await _discovery.StopWatchingAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error stopping discovery watcher.");
        }
    }
}
