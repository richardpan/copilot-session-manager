using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CopilotSessionManager.Core.Cli;
using CopilotSessionManager.Core.Cost;
using CopilotSessionManager.Core.GitHub;
using CopilotSessionManager.Core.GitHub.Checks;
using CopilotSessionManager.Core.GitHub.Issues;
using CopilotSessionManager.Core.GitHub.Storage;
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
    private readonly IGitHubClient? _githubClient;
    private readonly IGitHubChecksClient? _checksClient;
    private readonly ISessionLockCleanup? _lockCleanup;
    private readonly ISessionLauncher? _sessionLauncher;
    private readonly ILoggerFactory? _loggerFactory;
    private readonly ILogger<SessionsViewModel> _logger;
    private Action<SessionCardViewModel>? _openMergeWizard;

    #region IssueLinks
    private readonly IGitHubIssuesClient? _issuesClient;
    private readonly ISessionGitHubLinksStore? _linksStore;
    private readonly Func<string?, IssueRef?>? _showAddIssueDialog;
    private readonly IReadmeIssueRefProvider? _readmeIssueRefs;
    #endregion

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
            timeProvider, modelCatalog: null, costCalculator: null,
            githubClient: null, logger)
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
        : this(discovery, labelStore, readmeService, fileLauncher, dispatcher,
            timeProvider, modelCatalog, costCalculator, githubClient: null, logger)
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
        IGitHubClient? githubClient,
        ILogger<SessionsViewModel> logger)
        : this(discovery, labelStore, readmeService, fileLauncher, dispatcher,
            timeProvider, modelCatalog, costCalculator, githubClient,
            lockCleanup: null, sessionLauncher: null, loggerFactory: null, logger)
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
        IGitHubClient? githubClient,
        ISessionLockCleanup? lockCleanup,
        ISessionLauncher? sessionLauncher,
        ILoggerFactory? loggerFactory,
        ILogger<SessionsViewModel> logger)
        : this(discovery, labelStore, readmeService, fileLauncher, dispatcher,
            timeProvider, modelCatalog, costCalculator, githubClient,
            checksClient: null, lockCleanup, sessionLauncher, loggerFactory, logger)
    {
    }

    /// <summary>
    /// DI-preferred constructor: when <see cref="IGitHubChecksClient"/> is
    /// also registered (added by <c>AddGitHubLinks()</c>), this overload is
    /// selected because it has the most resolvable parameters. Older call
    /// sites that don't supply a checks client keep working via the
    /// <c>checksClient: null</c> chain above.
    /// </summary>
    public SessionsViewModel(
        ISessionDiscoveryService discovery,
        ISessionLabelStore labelStore,
        ISessionReadmeService readmeService,
        IFileLauncher fileLauncher,
        IUiDispatcher dispatcher,
        TimeProvider timeProvider,
        IModelCatalog? modelCatalog,
        IModelCostCalculator? costCalculator,
        IGitHubClient? githubClient,
        IGitHubChecksClient? checksClient,
        ISessionLockCleanup? lockCleanup,
        ISessionLauncher? sessionLauncher,
        ILoggerFactory? loggerFactory,
        ILogger<SessionsViewModel> logger)
        : this(discovery, labelStore, readmeService, fileLauncher, dispatcher,
            timeProvider, modelCatalog, costCalculator, githubClient, checksClient,
            lockCleanup, sessionLauncher, loggerFactory, logger,
            issuesClient: null, linksStore: null, showAddIssueDialog: null)
    {
    }

    /// <summary>
    /// DI-preferred constructor. Adds <see cref="IGitHubIssuesClient"/> and
    /// <see cref="ISessionGitHubLinksStore"/> for the manual issue-linking
    /// feature (#70). When <paramref name="showAddIssueDialog"/> is also
    /// provided by the host, each card gets a fully wired
    /// <see cref="IssueLinksViewModel"/>.
    /// </summary>
    public SessionsViewModel(
        ISessionDiscoveryService discovery,
        ISessionLabelStore labelStore,
        ISessionReadmeService readmeService,
        IFileLauncher fileLauncher,
        IUiDispatcher dispatcher,
        TimeProvider timeProvider,
        IModelCatalog? modelCatalog,
        IModelCostCalculator? costCalculator,
        IGitHubClient? githubClient,
        IGitHubChecksClient? checksClient,
        ISessionLockCleanup? lockCleanup,
        ISessionLauncher? sessionLauncher,
        ILoggerFactory? loggerFactory,
        ILogger<SessionsViewModel> logger,
        IGitHubIssuesClient? issuesClient,
        ISessionGitHubLinksStore? linksStore,
        Func<string?, IssueRef?>? showAddIssueDialog)
        : this(discovery, labelStore, readmeService, fileLauncher, dispatcher,
            timeProvider, modelCatalog, costCalculator, githubClient, checksClient,
            lockCleanup, sessionLauncher, loggerFactory, logger,
            issuesClient, linksStore, showAddIssueDialog, readmeIssueRefs: null)
    {
    }

    /// <summary>
    /// DI-preferred constructor. Adds <see cref="IReadmeIssueRefProvider"/>
    /// (#71) on top of the manual issue-linking plumbing so refs mentioned in
    /// the auto-generated <c>SESSION-README.md</c> appear as parsed badges.
    /// </summary>
    public SessionsViewModel(
        ISessionDiscoveryService discovery,
        ISessionLabelStore labelStore,
        ISessionReadmeService readmeService,
        IFileLauncher fileLauncher,
        IUiDispatcher dispatcher,
        TimeProvider timeProvider,
        IModelCatalog? modelCatalog,
        IModelCostCalculator? costCalculator,
        IGitHubClient? githubClient,
        IGitHubChecksClient? checksClient,
        ISessionLockCleanup? lockCleanup,
        ISessionLauncher? sessionLauncher,
        ILoggerFactory? loggerFactory,
        ILogger<SessionsViewModel> logger,
        IGitHubIssuesClient? issuesClient,
        ISessionGitHubLinksStore? linksStore,
        Func<string?, IssueRef?>? showAddIssueDialog,
        IReadmeIssueRefProvider? readmeIssueRefs)
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
        _githubClient = githubClient;
        _checksClient = checksClient;
        _lockCleanup = lockCleanup;
        _sessionLauncher = sessionLauncher;
        _loggerFactory = loggerFactory;
        _logger = logger;
        _issuesClient = issuesClient;
        _linksStore = linksStore;
        _showAddIssueDialog = showAddIssueDialog;
        _readmeIssueRefs = readmeIssueRefs;

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

    /// <summary>
    /// Wires the callback the WPF host uses to pop the merge wizard for a
    /// given source card. Set once at startup by <see cref="App"/> (the
    /// callback constructs <c>MergeWizardViewModel</c> + the
    /// <c>MergeWizard</c> window). Existing cards are reseated with the
    /// callback so they pick up the new <see cref="SessionCardViewModel.MergeIntoCommand"/>
    /// affordance immediately.
    /// </summary>
    public void SetMergeWizardLauncher(Action<SessionCardViewModel>? launcher)
    {
        _openMergeWizard = launcher;
        // Existing cards captured the previous (likely null) callback; rebuild
        // their MergeIntoCommand by re-calling UpdateFrom on the same model.
        // The card replaces the command in its constructor, so we can't mutate
        // it after the fact — but tests don't exercise this path, and the
        // host calls SetMergeWizardLauncher before the first scan completes
        // in practice. For belt-and-braces: we still rebuild the card list
        // when the launcher changes from non-null to non-null mid-flight.
        if (Sessions.Count == 0)
        {
            return;
        }
        // Rebuild the cards in place by replaying the snapshot. The next
        // discovery tick will overwrite them anyway.
        var snapshot = new List<Session>(Sessions.Count);
        foreach (var card in Sessions)
        {
            snapshot.Add(card.Model);
        }
        var labels = new Dictionary<string, SessionType>(StringComparer.OrdinalIgnoreCase);
        foreach (var card in Sessions)
        {
            labels[card.Id] = card.Label;
        }
        Sessions.Clear();
        _byId.Clear();
        ApplySnapshot(snapshot, labels);
    }

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
    /// Sweeps every session directory under <c>~/.copilot/session-state</c>
    /// and removes <c>inuse.*.lock</c> files whose owning PID is no longer
    /// running. Posts a status message summarising what changed and refreshes
    /// the dashboard so any newly-recovered sessions surface as Inactive.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanCleanAllStaleLocks))]
    public async Task CleanAllStaleLocksAsync(CancellationToken cancellationToken = default)
    {
        if (_lockCleanup is null)
        {
            return;
        }
        try
        {
            IsLoading = true;
            StatusMessage = "Cleaning stale lock files…";
            var result = await _lockCleanup.CleanupAllAsync(cancellationToken).ConfigureAwait(false);
            // Re-scan so the freshly-unlocked sessions report as Inactive.
            var sessions = await _discovery.ScanAsync(cancellationToken).ConfigureAwait(false);
            await ApplySnapshotAsync(sessions, cancellationToken).ConfigureAwait(false);
            // ApplySnapshotAsync overwrites StatusMessage with the post-scan
            // summary; replace it now so the user sees the cleanup outcome.
            StatusMessage = result.LocksRemoved == 0
                ? "No stale lock files found."
                : $"Removed {result.LocksRemoved} stale lock(s) across {result.SessionsAffected} session(s).";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Bulk lock cleanup failed.");
            StatusMessage = $"Cleanup failed: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private bool CanCleanAllStaleLocks() => _lockCleanup is not null;

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
                var cardLogger = _loggerFactory?.CreateLogger<SessionCardViewModel>();
                var issueLinks = TryCreateIssueLinks(session);
                var card = new SessionCardViewModel(
                    session, label, _timeProvider, _modelCatalog, _costCalculator,
                    _fileLauncher, _lockCleanup, _sessionLauncher, cardLogger,
                    _openMergeWizard, issueLinks);
                _byId[session.Id] = card;
                Sessions.Add(card);
                inserted = true;

                if (issueLinks is not null)
                {
                    _ = issueLinks.LoadAsync();
                }
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

        // Kick off PR lookups off the UI thread; results are pushed back to
        // each card on the dispatcher. Lookups are best-effort — failures are
        // swallowed and just leave the badge empty.
        QueuePullRequestLookups(snapshot);
    }

    private void QueuePullRequestLookups(IReadOnlyList<Session> snapshot)
    {
        if (_githubClient is null)
        {
            return;
        }

        foreach (var session in snapshot)
        {
            var links = session.GitHubLinks;
            var repoSlug = ExtractSlug(links?.RepositoryUrl);
            if (repoSlug is null || string.IsNullOrWhiteSpace(session.Branch))
            {
                continue;
            }

            var sessionId = session.Id;
            var branch = session.Branch!;

            _ = Task.Run(async () =>
            {
                try
                {
                    var pr = await _githubClient.FindPullRequestAsync(repoSlug, branch, CancellationToken.None)
                        .ConfigureAwait(false);
                    _dispatcher.Post(() =>
                    {
                        if (_byId.TryGetValue(sessionId, out var card))
                        {
                            card.SetPullRequest(pr);
                        }
                    });

                    // Once we have a PR number, kick off the CI rollup
                    // probe. Best-effort and independent — failures here
                    // just leave the check badge empty.
                    if (pr is not null && _checksClient is not null)
                    {
                        try
                        {
                            var checks = await _checksClient
                                .GetChecksAsync(repoSlug, pr.Number, CancellationToken.None)
                                .ConfigureAwait(false);
                            _dispatcher.Post(() =>
                            {
                                if (_byId.TryGetValue(sessionId, out var card))
                                {
                                    card.SetChecks(checks);
                                }
                            });
                        }
                        catch (Exception checksEx)
                        {
                            _logger.LogDebug(
                                checksEx,
                                "PR checks lookup failed for {Slug}#{Pr}",
                                repoSlug,
                                pr.Number);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "PR lookup failed for {Slug}@{Branch}", repoSlug, branch);
                }
            });
        }
    }

    private static string? ExtractSlug(string? repositoryUrl)
    {
        if (string.IsNullOrWhiteSpace(repositoryUrl))
        {
            return null;
        }
        const string prefix = "https://github.com/";
        return repositoryUrl.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? repositoryUrl[prefix.Length..]
            : null;
    }

    #region IssueLinks
    private IssueLinksViewModel? TryCreateIssueLinks(Session session)
    {
        // Without a dialog callback the panel can't add new issues; without
        // an issues client we can't fetch metadata; without a links store we
        // can't persist. We need at minimum the dialog + store to be useful,
        // and the issues client for metadata enrichment. If any are missing
        // we degrade gracefully — still show the panel if we have a store
        // and a dialog (so previously linked issues hydrate), otherwise
        // skip entirely so the panel doesn't render.
        if (_linksStore is null && _issuesClient is null && _readmeIssueRefs is null)
        {
            return null;
        }

        var defaultSlug = ExtractSlug(session.GitHubLinks?.RepositoryUrl);
        var dialog = _showAddIssueDialog ?? (static (_) => null);
        var logger = _loggerFactory?.CreateLogger<IssueLinksViewModel>();
        return new IssueLinksViewModel(
            session.Id,
            defaultSlug,
            _issuesClient,
            _linksStore,
            _fileLauncher,
            _dispatcher,
            dialog,
            logger,
            _readmeIssueRefs);
    }
    #endregion

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
