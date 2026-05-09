using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CopilotSessionManager.Core.Cli;
using CopilotSessionManager.Core.Cost;
using CopilotSessionManager.Core.Models;
using CopilotSessionManager.Core.Sessions;
using CopilotSessionManager.Services;
using Microsoft.Extensions.Logging;

namespace CopilotSessionManager.ViewModels;

/// <summary>
/// Top-level view model behind the sessions dashboard. Owns the live
/// <see cref="ObservableCollection{T}"/> of <see cref="SessionCardViewModel"/>,
/// subscribes to <see cref="ISessionDiscoveryService.SessionsChanged"/>, and
/// applies the active-only + label filters.
/// </summary>
public sealed partial class SessionsViewModel : ObservableObject, IAsyncDisposable
{
    private readonly ISessionDiscoveryService _discovery;
    private readonly ISessionLabelStore _labelStore;
    private readonly ISessionReadmeService _readmeService;
    private readonly IFileLauncher _fileLauncher;
    private readonly IUiDispatcher _dispatcher;
    private readonly TimeProvider _timeProvider;
    private readonly IModelCatalog? _modelCatalog;
    private readonly IModelCostCalculator? _costCalculator;
    private readonly ILogger<SessionsViewModel> _logger;

    private readonly Dictionary<string, SessionCardViewModel> _byId =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<SessionType> _hiddenLabels = new();
    private readonly HashSet<ModelTier> _hiddenTiers = new();
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
        ISessionLabelStore labelStore,
        ISessionReadmeService readmeService,
        IFileLauncher fileLauncher,
        IUiDispatcher dispatcher,
        TimeProvider timeProvider,
        ILogger<SessionsViewModel> logger)
        : this(discovery, labelStore, readmeService, fileLauncher, dispatcher,
            timeProvider, modelCatalog: null, costCalculator: null, logger)
    {
    }

    public SessionsViewModel(
        ISessionDiscoveryService discovery,
        ISessionLabelStore labelStore,
        ISessionReadmeService readmeService,
        IFileLauncher fileLauncher,
        IUiDispatcher dispatcher,
        TimeProvider timeProvider,
        IModelCatalog? modelCatalog,
        IModelCostCalculator? costCalculator,
        ILogger<SessionsViewModel> logger)
    {
        ArgumentNullException.ThrowIfNull(discovery);
        ArgumentNullException.ThrowIfNull(labelStore);
        ArgumentNullException.ThrowIfNull(readmeService);
        ArgumentNullException.ThrowIfNull(fileLauncher);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _discovery = discovery;
        _labelStore = labelStore;
        _readmeService = readmeService;
        _fileLauncher = fileLauncher;
        _dispatcher = dispatcher;
        _timeProvider = timeProvider;
        _modelCatalog = modelCatalog;
        _costCalculator = costCalculator;
        _logger = logger;

        Sessions = new ObservableCollection<SessionCardViewModel>();
        VisibleSessions = new ObservableCollection<SessionCardViewModel>();
        LabelFilters = new ObservableCollection<LabelFilterChip>();
        foreach (var t in Enum.GetValues<SessionType>())
        {
            LabelFilters.Add(new LabelFilterChip(t, isVisible: true, this));
        }
        TierFilters = new ObservableCollection<TierFilterChip>();
        foreach (var t in Enum.GetValues<ModelTier>())
        {
            TierFilters.Add(new TierFilterChip(t, isVisible: true, this));
        }
    }

    /// <summary>The full set of discovered sessions (unfiltered).</summary>
    public ObservableCollection<SessionCardViewModel> Sessions { get; }

    /// <summary>Sessions after the active-only + label filters have been applied.</summary>
    public ObservableCollection<SessionCardViewModel> VisibleSessions { get; }

    /// <summary>One filter chip per <see cref="SessionType"/>.</summary>
    public ObservableCollection<LabelFilterChip> LabelFilters { get; }

    /// <summary>One filter chip per <see cref="ModelTier"/>.</summary>
    public ObservableCollection<TierFilterChip> TierFilters { get; }

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
            _labelStore.LabelChanged += OnLabelChangedFromStore;
            await _discovery.StartWatchingAsync(cancellationToken).ConfigureAwait(false);

            // StartWatchingAsync does an initial scan internally and updates
            // CurrentSessions; mirror it now so the UI has data immediately.
            await ApplySnapshotAsync(_discovery.CurrentSessions, cancellationToken)
                .ConfigureAwait(false);
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
            await ApplySnapshotAsync(sessions, cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    /// Persists <paramref name="type"/> as the user-assigned label for
    /// <paramref name="card"/>. The store will raise <see cref="ISessionLabelStore.LabelChanged"/>,
    /// which updates the matching card on the UI thread.
    /// </summary>
    public async Task SetLabelAsync(
        SessionCardViewModel card,
        SessionType type,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(card);
        try
        {
            await _labelStore.SetAsync(card.Id, type, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to set label for session {Id}.", card.Id);
            StatusMessage = $"Could not set label: {ex.Message}";
        }
    }

    /// <summary>
    /// Ensures <c>SESSION-README.md</c> exists for <paramref name="card"/>'s
    /// session — generating or refreshing it via <see cref="ISessionReadmeService"/> —
    /// and then opens it with the OS default handler.
    /// </summary>
    [RelayCommand]
    public async Task OpenReadmeAsync(SessionCardViewModel? card, CancellationToken cancellationToken = default)
    {
        if (card is null)
        {
            return;
        }

        try
        {
            await _readmeService.EnsureAsync(card.Model, card.Label, cancellationToken).ConfigureAwait(false);
            var path = _readmeService.GetReadmePath(card.Id);
            await _fileLauncher.OpenAsync(path, cancellationToken).ConfigureAwait(false);
            StatusMessage = $"Opened README for {card.ShortId}.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open README for session {Id}.", card.Id);
            StatusMessage = $"Could not open README: {ex.Message}";
        }
    }

    partial void OnShowInactiveChanged(bool value) => RebuildVisible();

    private void OnSessionsChanged(object? sender, SessionsChangedEventArgs e)
    {
        // Marshal to the UI thread so ObservableCollection mutations are safe.
        _dispatcher.Post(() =>
        {
            // Fire-and-forget: ApplySnapshotAsync awaits the label store but
            // mutations of the observable collections happen synchronously
            // before the first await, then again after labels are loaded.
            _ = ApplySnapshotAsync(e.Sessions, CancellationToken.None);
        });
    }

    private void OnLabelChangedFromStore(object? sender, SessionLabelChangedEventArgs e)
    {
        _dispatcher.Post(() =>
        {
            if (_byId.TryGetValue(e.SessionId, out var card))
            {
                card.UpdateLabel(e.NewType);
                if (_hiddenLabels.Count > 0)
                {
                    RebuildVisible();
                }
            }
        });
    }

    private async Task ApplySnapshotAsync(
        IReadOnlyList<Session> snapshot,
        CancellationToken cancellationToken)
    {
        // Pre-fetch labels for any newly-seen sessions before we touch the UI
        // collections so the first paint already has the right chip color.
        var newLabels = new Dictionary<string, SessionType>(StringComparer.OrdinalIgnoreCase);
        foreach (var session in snapshot)
        {
            if (!_byId.ContainsKey(session.Id))
            {
                try
                {
                    newLabels[session.Id] = await _labelStore
                        .GetAsync(session.Id, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Could not read label for {Id}; using default.", session.Id);
                    newLabels[session.Id] = SessionType.Exploratory;
                }
            }
        }

        _dispatcher.Post(() => ApplySnapshot(snapshot, newLabels));
    }

    private void ApplySnapshot(
        IReadOnlyList<Session> snapshot,
        IReadOnlyDictionary<string, SessionType> newLabels)
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
                var label = newLabels.TryGetValue(session.Id, out var t) ? t : SessionType.Exploratory;
                var card = new SessionCardViewModel(session, label, _timeProvider, _modelCatalog, _costCalculator);
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
            if ((ShowInactive || IsActive(card.Status))
                && !_hiddenLabels.Contains(card.Label)
                && !_hiddenTiers.Contains(card.ModelTier))
            {
                VisibleSessions.Add(card);
            }
        }
    }

    /// <summary>
    /// Toggles whether sessions of <paramref name="type"/> are visible. Used
    /// by <see cref="LabelFilterChip"/> bindings.
    /// </summary>
    internal void SetLabelVisible(SessionType type, bool isVisible)
    {
        var changed = isVisible ? _hiddenLabels.Remove(type) : _hiddenLabels.Add(type);
        if (changed)
        {
            RebuildVisible();
        }
    }

    /// <summary>
    /// Toggles whether sessions of <paramref name="tier"/> are visible. Used
    /// by <see cref="TierFilterChip"/> bindings.
    /// </summary>
    internal void SetTierVisible(ModelTier tier, bool isVisible)
    {
        var changed = isVisible ? _hiddenTiers.Remove(tier) : _hiddenTiers.Add(tier);
        if (changed)
        {
            RebuildVisible();
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
        _labelStore.LabelChanged -= OnLabelChangedFromStore;
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

/// <summary>
/// Bindable toggle representing one <see cref="SessionType"/> in the dashboard
/// filter row. Two-way bound to a CheckBox; setting <see cref="IsVisible"/>
/// calls back into <see cref="SessionsViewModel.SetLabelVisible"/>.
/// </summary>
public sealed partial class LabelFilterChip : ObservableObject
{
    private readonly SessionsViewModel _owner;

    [ObservableProperty]
    private bool _isVisible;

    public LabelFilterChip(SessionType type, bool isVisible, SessionsViewModel owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        Type = type;
        _isVisible = isVisible;
        _owner = owner;
    }

    public SessionType Type { get; }

    public string Label => Type switch
    {
        SessionType.Exploratory => "Exploratory",
        SessionType.Research => "Research",
        SessionType.Feature => "Feature",
        SessionType.Bug => "Bug",
        SessionType.Refactor => "Refactor",
        SessionType.Docs => "Docs",
        SessionType.Infra => "Infra",
        SessionType.Experiment => "Experiment",
        _ => Type.ToString(),
    };

    partial void OnIsVisibleChanged(bool value) => _owner.SetLabelVisible(Type, value);
}

/// <summary>
/// Bindable toggle representing one <see cref="ModelTier"/> in the dashboard
/// filter row. Two-way bound to a CheckBox; setting <see cref="IsVisible"/>
/// calls back into <see cref="SessionsViewModel.SetTierVisible"/>.
/// </summary>
public sealed partial class TierFilterChip : ObservableObject
{
    private readonly SessionsViewModel _owner;

    [ObservableProperty]
    private bool _isVisible;

    public TierFilterChip(ModelTier tier, bool isVisible, SessionsViewModel owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        Tier = tier;
        _isVisible = isVisible;
        _owner = owner;
    }

    public ModelTier Tier { get; }

    public string Label => Tier switch
    {
        ModelTier.Premium => "Premium",
        ModelTier.Standard => "Standard",
        ModelTier.Fast => "Fast",
        _ => "Unknown",
    };

    partial void OnIsVisibleChanged(bool value) => _owner.SetTierVisible(Tier, value);
}
