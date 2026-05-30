using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
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
    private Action<SessionCardViewModel>? _openEmbeddedTerminal;
    private Action? _openNewEmbeddedCopilotTab;
    private Action<string>? _tabClosedOnDelete;

    #region IssueLinks
    private readonly IGitHubIssuesClient? _issuesClient;
    private readonly ISessionGitHubLinksStore? _linksStore;
    private readonly Func<string?, IssueRef?>? _showAddIssueDialog;
    private readonly IReadmeIssueRefProvider? _readmeIssueRefs;
    #endregion

    #region SessionManagement
    // V1.1 polish: open / rename / delete plumbing (#104, #105, #106).
    private readonly IRunningSessionRegistry? _runningSessions;
    private readonly Native.IWindowActivator? _windowActivator;
    private readonly ISessionDisplayNameStore? _displayNameStore;
    private readonly ISessionStarStore? _starStore;
    private readonly ISessionDeletionService? _deletionService;
    private readonly Func<SessionDeletionPrompt, bool>? _confirmDelete;
    private readonly IDocFreshnessService? _docFreshness;
    private readonly IWrapUpStateStore? _wrapUpStateStore;
    private readonly IClipboardService? _clipboardService;
    private readonly Core.Settings.AppSettings? _appSettings;
    #endregion

    // V1.6 (#118): generated HTML session docs. Settable post-construction
    // via SetDocsService so we don't churn the canonical ctor chain.
    private ISessionDocsService? _docsService;

    private readonly Dictionary<string, SessionCardViewModel> _byId =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<SessionCardViewModel> _crashBannerObservedCards = new();
    private readonly HashSet<SessionType> _hiddenLabels = new();
    private readonly HashSet<ModelTier> _hiddenTiers = new();
    // V1.3 (#148): doc-freshness chip visibility. The bucket enum collapses
    // Stale + VeryStale into a single user-facing "Stale" chip.
    private readonly HashSet<DocFreshnessFilterBucket> _hiddenDocFreshness = new();
    private bool _started;
    private bool _disposed;

    [ObservableProperty]
    private bool _showInactive = true;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _statusMessage = "Ready.";

    /// <summary>
    /// V1.3 (#110): free-text filter applied on top of label/tier/inactive
    /// filters. Whitespace-separated tokens are AND-matched (each token
    /// must hit) case-insensitively against the card's DisplayName or its
    /// original Copilot Title, so renamed sessions still surface for
    /// searches against the original summary. Empty/whitespace value is a
    /// match-all (preserves the prior behaviour).
    /// </summary>
    [ObservableProperty]
    private string _searchText = string.Empty;

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
        : this(discovery, labelStore, readmeService, fileLauncher, dispatcher,
            timeProvider, modelCatalog, costCalculator, githubClient, checksClient,
            lockCleanup, sessionLauncher, loggerFactory, logger,
            issuesClient, linksStore, showAddIssueDialog, readmeIssueRefs,
            runningSessions: null, windowActivator: null,
            displayNameStore: null, deletionService: null, confirmDelete: null)
    {
    }

    /// <summary>
    /// V1.1 canonical constructor (#104, #105, #106). Adds the optional
    /// open / rename / delete services on top of the existing plumbing. All
    /// new parameters are nullable so existing call sites and tests keep
    /// working unchanged.
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
        IReadmeIssueRefProvider? readmeIssueRefs,
        IRunningSessionRegistry? runningSessions,
        Native.IWindowActivator? windowActivator,
        ISessionDisplayNameStore? displayNameStore,
        ISessionDeletionService? deletionService,
        Func<SessionDeletionPrompt, bool>? confirmDelete)
        : this(discovery, labelStore, readmeService, fileLauncher, dispatcher,
            timeProvider, modelCatalog, costCalculator, githubClient, checksClient,
            lockCleanup, sessionLauncher, loggerFactory, logger,
            issuesClient, linksStore, showAddIssueDialog, readmeIssueRefs,
            runningSessions, windowActivator, displayNameStore, deletionService,
            confirmDelete, starStore: null)
    {
    }

    /// <summary>
    /// V1.4 canonical constructor (#112, #113). Adds the optional
    /// <see cref="ISessionStarStore"/> for the "pin to top" feature on top of
    /// the V1.1 plumbing. <paramref name="starStore"/> is nullable so test
    /// fixtures and older call sites keep compiling.
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
        IReadmeIssueRefProvider? readmeIssueRefs,
        IRunningSessionRegistry? runningSessions,
        Native.IWindowActivator? windowActivator,
        ISessionDisplayNameStore? displayNameStore,
        ISessionDeletionService? deletionService,
        Func<SessionDeletionPrompt, bool>? confirmDelete,
        ISessionStarStore? starStore)
        : this(discovery, labelStore, readmeService, fileLauncher, dispatcher,
            timeProvider, modelCatalog, costCalculator, githubClient, checksClient,
            lockCleanup, sessionLauncher, loggerFactory, logger,
            issuesClient, linksStore, showAddIssueDialog, readmeIssueRefs,
            runningSessions, windowActivator, displayNameStore, deletionService,
            confirmDelete, starStore, docFreshness: null)
    {
    }

    /// <summary>
    /// V1.3 (#147) canonical constructor adding the optional
    /// <see cref="IDocFreshnessService"/> used by each card to populate the
    /// "Docs" freshness badge column. Nullable so existing tests and call
    /// sites keep working.
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
        IReadmeIssueRefProvider? readmeIssueRefs,
        IRunningSessionRegistry? runningSessions,
        Native.IWindowActivator? windowActivator,
        ISessionDisplayNameStore? displayNameStore,
        ISessionDeletionService? deletionService,
        Func<SessionDeletionPrompt, bool>? confirmDelete,
        ISessionStarStore? starStore,
        IDocFreshnessService? docFreshness)
        : this(discovery, labelStore, readmeService, fileLauncher, dispatcher,
            timeProvider, modelCatalog, costCalculator, githubClient, checksClient,
            lockCleanup, sessionLauncher, loggerFactory, logger,
            issuesClient, linksStore, showAddIssueDialog, readmeIssueRefs,
            runningSessions, windowActivator, displayNameStore, deletionService,
            confirmDelete, starStore, docFreshness,
            wrapUpStateStore: null, clipboardService: null, appSettings: null)
    {
    }

    /// <summary>
    /// V1.3 (#149) canonical constructor adding the optional
    /// <see cref="IWrapUpStateStore"/> + <see cref="IClipboardService"/>
    /// + <see cref="Core.Settings.AppSettings"/> trio that powers the
    /// "📝 Wrap up" launcher badge on each session card. All three are
    /// nullable so older call sites and tests keep compiling.
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
        IReadmeIssueRefProvider? readmeIssueRefs,
        IRunningSessionRegistry? runningSessions,
        Native.IWindowActivator? windowActivator,
        ISessionDisplayNameStore? displayNameStore,
        ISessionDeletionService? deletionService,
        Func<SessionDeletionPrompt, bool>? confirmDelete,
        ISessionStarStore? starStore,
        IDocFreshnessService? docFreshness,
        IWrapUpStateStore? wrapUpStateStore,
        IClipboardService? clipboardService,
        Core.Settings.AppSettings? appSettings)
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
        _runningSessions = runningSessions;
        _windowActivator = windowActivator;
        _displayNameStore = displayNameStore;
        _starStore = starStore;
        _deletionService = deletionService;
        _confirmDelete = confirmDelete;
        _docFreshness = docFreshness;
        _wrapUpStateStore = wrapUpStateStore;
        _clipboardService = clipboardService;
        _appSettings = appSettings;

        if (_displayNameStore is not null)
        {
            _displayNameStore.DisplayNameChanged += OnDisplayNameStoreChanged;
        }

        if (_starStore is not null)
        {
            _starStore.StarsChanged += OnStarStoreChanged;
        }

        if (_wrapUpStateStore is not null)
        {
            _wrapUpStateStore.WrapUpStateChanged += OnWrapUpStateStoreChanged;
        }

        Sessions = new ObservableCollection<SessionCardViewModel>();
        VisibleSessions = new ObservableCollection<SessionCardViewModel>();
        CrashBanner = new CrashBannerViewModel(this);
        Sessions.CollectionChanged += OnSessionCardsCollectionChanged;
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
        DocsFilters = new ObservableCollection<DocsFilterChip>();
        foreach (var b in Enum.GetValues<DocFreshnessFilterBucket>())
        {
            DocsFilters.Add(new DocsFilterChip(b, isVisible: true, this));
        }
    }

    private void OnDisplayNameStoreChanged(object? sender, SessionDisplayNameChangedEventArgs e)
    {
        // Push back to the dispatcher because the store can fire from any thread.
        _dispatcher.Post(() =>
        {
            if (_byId.TryGetValue(e.SessionId, out var card))
            {
                card.ApplyDisplayNameOverride(e.NewDisplayName);
            }
        });
    }

    /// <summary>The full set of discovered sessions (unfiltered).</summary>
    public ObservableCollection<SessionCardViewModel> Sessions { get; }

    /// <summary>Sessions after the active-only + label filters have been applied.</summary>
    public ObservableCollection<SessionCardViewModel> VisibleSessions { get; }

    /// <summary>Notification banner shown when crashed sessions need attention.</summary>
    public CrashBannerViewModel CrashBanner { get; }

    /// <summary>One filter chip per <see cref="SessionType"/>.</summary>
    public ObservableCollection<LabelFilterChip> LabelFilters { get; }

    /// <summary>One filter chip per <see cref="ModelTier"/>.</summary>
    public ObservableCollection<TierFilterChip> TierFilters { get; }

    /// <summary>
    /// V1.3 (#148): one filter chip per <see cref="DocFreshnessFilterBucket"/>
    /// (Fresh / Stale / Missing / N/A). Stale + VeryStale share a single
    /// "Stale" bucket because the user-facing column already collapses them
    /// to the same amber badge.
    /// </summary>
    public ObservableCollection<DocsFilterChip> DocsFilters { get; }

    /// <summary>
    /// V1.2.3 (#142): caption shown on the Labels filter dropdown — e.g.
    /// "Labels (all)", "Labels (3 of 8)", or "Labels (none)". Updates in
    /// response to per-chip <see cref="LabelFilterChip.IsVisible"/> changes.
    /// </summary>
    public string LabelsFilterSummary => FormatFilterSummary(
        "Labels", LabelFilters.Count(c => c.IsVisible), LabelFilters.Count);

    /// <summary>
    /// V1.2.3 (#142): caption shown on the Tiers filter dropdown.
    /// </summary>
    public string TiersFilterSummary => FormatFilterSummary(
        "Tiers", TierFilters.Count(c => c.IsVisible), TierFilters.Count);

    /// <summary>
    /// V1.3 (#148): caption shown on the Docs filter dropdown — e.g.
    /// "Docs (all)", "Docs (3 of 4)", or "Docs (none)". Updates in
    /// response to per-chip <see cref="DocsFilterChip.IsVisible"/> changes.
    /// </summary>
    public string DocsFilterSummary => FormatFilterSummary(
        "Docs", DocsFilters.Count(c => c.IsVisible), DocsFilters.Count);

    private static string FormatFilterSummary(string label, int visible, int total)
    {
        if (total == 0)
        {
            return $"{label} (none)";
        }
        if (visible == total)
        {
            return $"{label} (all)";
        }
        if (visible == 0)
        {
            return $"{label} (none)";
        }
        return $"{label} ({visible} of {total})";
    }

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
        ApplySnapshot(snapshot, labels, new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase), new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase), new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// V1.4 (#159): wires the callback the WPF host uses to route a
    /// card's <see cref="SessionCardViewModel.OpenCommand"/> into the
    /// embedded tabbed terminal view. Set once at startup by
    /// <see cref="App"/>. Existing cards captured the previous (likely
    /// null) callback, so when the callback changes from null to
    /// non-null mid-flight we replay the current session snapshot to
    /// rebuild every card with the new callback wired — the same
    /// belt-and-braces approach <see cref="SetMergeWizardLauncher"/>
    /// uses for the same reason.
    /// </summary>
    public void SetOpenEmbeddedTerminalCallback(Action<SessionCardViewModel>? callback)
    {
        _openEmbeddedTerminal = callback;
        if (Sessions.Count == 0)
        {
            return;
        }
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
        ApplySnapshot(snapshot, labels, new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase), new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase), new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// V1.5 — host hook for opening a brand-new Copilot session inside
    /// the embedded tabbed terminal view. Wired by <see cref="App"/>
    /// to a <c>TerminalTabsViewModel.OpenNewCopilotTab(...)</c> call.
    /// When set, <see cref="NewSessionAsync"/> uses this in preference
    /// to the external PowerShell launcher; right-clicking the New
    /// session button still routes to the external launcher via
    /// <see cref="NewSessionExternalAsync"/>.
    /// </summary>
    public void SetOpenNewEmbeddedCopilotTabCallback(Action? callback)
    {
        _openNewEmbeddedCopilotTab = callback;
        NewSessionCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Phase 6D (#159) lifecycle hook: when a session card is removed
    /// from the dashboard (delete confirmed), invoke
    /// <paramref name="callback"/> with the removed session id so the
    /// host can close any embedded terminal tab tied to it. The
    /// callback runs after the card has been removed from
    /// <see cref="Sessions"/>; exceptions are logged and swallowed at
    /// the call site.
    /// </summary>
    public void SetTabClosedOnDeleteCallback(Action<string>? callback)
    {
        _tabClosedOnDelete = callback;
    }

    /// <summary>
    /// Phase 6D (#159): currently-selected card in the dashboard
    /// DataGrid. Bound TwoWay to <c>DataGrid.SelectedItem</c> in the
    /// view so the host can drive selection programmatically and react
    /// to user-driven selection changes via
    /// <see cref="System.ComponentModel.INotifyPropertyChanged"/>.
    /// May be <c>null</c> when no row is selected or when the grid is
    /// empty.
    /// </summary>
    [ObservableProperty]
    private SessionCardViewModel? _selectedCard;

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
    /// return immediately. When <paramref name="autoCleanStaleLocksOnStartup"/>
    /// is <c>true</c>, the equivalent of the toolbar
    /// <c>🧹 Clean stale locks</c> command runs once after the first scan
    /// succeeds — useful for users who never want to think about lingering
    /// <c>inuse.&lt;pid&gt;.lock</c> files after a CLI crash.
    /// </summary>
    public async Task InitializeAsync(
        bool autoCleanStaleLocksOnStartup = false,
        CancellationToken cancellationToken = default)
    {
        if (_started)
        {
            return;
        }
        _started = true;

        var initialScanSucceeded = false;

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
            initialScanSucceeded = true;
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

        // V1.8 (#74): opt-in startup sweep of stale locks. Runs only after a
        // successful initial scan so the user never sees a "Cleaning…" status
        // before the first session list has even rendered. Failures inside
        // CleanAllStaleLocksAsync are already swallowed and surfaced via
        // StatusMessage there, so we don't double-handle.
        if (initialScanSucceeded
            && autoCleanStaleLocksOnStartup
            && _lockCleanup is not null)
        {
            await CleanAllStaleLocksAsync(cancellationToken).ConfigureAwait(false);
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
    /// V1.2 (#108): launches a fresh PowerShell window running <c>copilot</c>
    /// with no <c>--resume</c>, so the CLI mints a brand-new session id.
    /// We don't know that id at launch time, so we defer a refresh ~3s
    /// later to surface the new card without waiting for the discovery
    /// FileSystemWatcher debounce. Disabled when no launcher is wired
    /// (test ctors).
    /// </summary>
    /// <summary>
    /// V1.5 — the default "New session" affordance now opens a brand-new
    /// Copilot session inside the embedded tabbed terminal view when the
    /// host has wired <see cref="SetOpenNewEmbeddedCopilotTabCallback"/>.
    /// If the embedded callback isn't wired (older hosts, tests), falls
    /// back to the V1.3 external-PowerShell flow that
    /// <see cref="NewSessionExternalAsync"/> still exposes via the
    /// right-click menu.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanNewSession))]
    public async Task NewSessionAsync(CancellationToken cancellationToken = default)
    {
        if (_openNewEmbeddedCopilotTab is not null)
        {
            try
            {
                _openNewEmbeddedCopilotTab();
                StatusMessage = "Opened new Copilot session in embedded tab. Refreshing…";
                ScheduleDeferredRefresh(cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to open new embedded Copilot session.");
                StatusMessage = $"Could not open embedded session: {ex.Message}";
            }
            return;
        }

        await NewSessionExternalAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// V1.5 — explicit external-PowerShell launch path for "New session".
    /// Wired to the right-click context menu entry on the New session
    /// button so users can still opt out of the embedded default. Same
    /// behaviour as the pre-V1.5 default click.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanNewSessionExternal))]
    public async Task NewSessionExternalAsync(CancellationToken cancellationToken = default)
    {
        if (_sessionLauncher is null)
        {
            return;
        }
        try
        {
            StatusMessage = "Launching new Copilot session…";
            var result = await _sessionLauncher.LaunchNewAsync(workingDirectory: null, cancellationToken)
                .ConfigureAwait(false);
            StatusMessage = result.ProcessId is int pid
                ? $"Launched new Copilot session (pid {pid}). Refreshing…"
                : "Launched new Copilot session. Refreshing…";
            ScheduleDeferredRefresh(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to launch new Copilot session.");
            StatusMessage = $"Could not launch new session: {ex.Message}";
        }
    }

    /// <summary>
    /// Defer a dashboard rescan by ~3 seconds so the Copilot CLI has time
    /// to write its new <c>session-state</c> directory before discovery
    /// runs again. Shared by both <see cref="NewSessionAsync"/> and
    /// <see cref="NewSessionExternalAsync"/>.
    /// </summary>
    private void ScheduleDeferredRefresh(CancellationToken cancellationToken)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
                await RefreshAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Cancelled — nothing to do.
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Post-launch refresh failed.");
            }
        }, cancellationToken);
    }

    private bool CanNewSession() => _openNewEmbeddedCopilotTab is not null || _sessionLauncher is not null;

    private bool CanNewSessionExternal() => _sessionLauncher is not null;

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

    /// <summary>
    /// V1.6 (#118): Wires the docs service that V1.6 ships. Called once
    /// from <c>App.OnStartup</c> after the host has built. Optional so
    /// existing test fixtures and older constructor chains keep working.
    /// </summary>
    public void SetDocsService(ISessionDocsService? docsService)
    {
        _docsService = docsService;
    }

    /// <summary>
    /// V1.5 (#194 follow-up): Opens the agent-curated <c>plan.md</c> for
    /// <paramref name="card"/>'s session in the user's default editor
    /// (whatever handler is registered for <c>.md</c> — VS Code, Notepad,
    /// etc.). If no <c>plan.md</c> exists yet — common for sessions that
    /// have not yet engaged the Copilot planner — falls back to the V1.6
    /// SESSION-DOCS.html flow so the button never feels broken. No-op if
    /// the docs service has not been wired (e.g. legacy test fixtures).
    /// </summary>
    [RelayCommand]
    public async Task OpenDocsAsync(SessionCardViewModel? card, CancellationToken cancellationToken = default)
    {
        if (card is null)
        {
            return;
        }

        if (_docsService is null)
        {
            StatusMessage = "Docs service is not available.";
            return;
        }

        try
        {
            var planPath = _docsService.GetPlanMarkdownPath(card.Id);
            if (File.Exists(planPath))
            {
                await _fileLauncher.OpenAsync(planPath, cancellationToken).ConfigureAwait(false);
                StatusMessage = $"Opened plan.md for {card.ShortId} in your default editor.";
                return;
            }

            var htmlPath = await _docsService.EnsureAsync(card.Model, cancellationToken).ConfigureAwait(false);
            await _fileLauncher.OpenAsync(htmlPath, cancellationToken).ConfigureAwait(false);
            StatusMessage = $"No plan.md yet for {card.ShortId} — opened SESSION-DOCS.html instead.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open docs for session {Id}.", card.Id);
            StatusMessage = $"Could not open session docs: {ex.Message}";
        }
    }

    partial void OnShowInactiveChanged(bool value) => RebuildVisible();

    partial void OnSearchTextChanged(string value) => RebuildVisible();

    private void OnSessionCardsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        SyncCrashBannerSubscriptions();
        CrashBanner.Refresh();
    }

    private void OnCrashBannerCardPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SessionCardViewModel.IsCrashed))
        {
            CrashBanner.Refresh();
        }
    }

    private void SyncCrashBannerSubscriptions()
    {
        var currentCards = new HashSet<SessionCardViewModel>(Sessions);
        foreach (var observed in _crashBannerObservedCards.ToArray())
        {
            if (!currentCards.Contains(observed))
            {
                observed.PropertyChanged -= OnCrashBannerCardPropertyChanged;
                _crashBannerObservedCards.Remove(observed);
            }
        }

        foreach (var card in Sessions)
        {
            if (_crashBannerObservedCards.Add(card))
            {
                card.PropertyChanged += OnCrashBannerCardPropertyChanged;
            }
        }
    }

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
        var newDisplayNames = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var newStars = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var newWrapUps = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
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

                if (_displayNameStore is not null)
                {
                    try
                    {
                        newDisplayNames[session.Id] = await _displayNameStore
                            .GetAsync(session.Id, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Could not read display-name override for {Id}.", session.Id);
                        newDisplayNames[session.Id] = null;
                    }
                }

                if (_starStore is not null)
                {
                    try
                    {
                        newStars[session.Id] = await _starStore
                            .IsStarredAsync(session.Id, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Could not read star state for {Id}.", session.Id);
                        newStars[session.Id] = false;
                    }
                }

                if (_wrapUpStateStore is not null)
                {
                    try
                    {
                        var ts = await _wrapUpStateStore
                            .GetRequestedAtAsync(session.Id, cancellationToken)
                            .ConfigureAwait(false);
                        if (ts.HasValue)
                        {
                            newWrapUps[session.Id] = ts.Value;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Could not read wrap-up state for {Id}.", session.Id);
                    }
                }
            }
        }

        _dispatcher.Post(() => ApplySnapshot(snapshot, newLabels, newDisplayNames, newStars, newWrapUps));
    }

    private void ApplySnapshot(
        IReadOnlyList<Session> snapshot,
        IReadOnlyDictionary<string, SessionType> newLabels,
        IReadOnlyDictionary<string, string?> newDisplayNames,
        IReadOnlyDictionary<string, bool> newStars,
        IReadOnlyDictionary<string, DateTimeOffset> newWrapUps)
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
                var displayName = newDisplayNames.TryGetValue(session.Id, out var d) ? d : null;
                var isStarred = newStars.TryGetValue(session.Id, out var s) && s;
                var cardLogger = _loggerFactory?.CreateLogger<SessionCardViewModel>();
                var issueLinks = TryCreateIssueLinks(session);
                var card = new SessionCardViewModel(
                    session, label, _timeProvider, _modelCatalog, _costCalculator,
                    _fileLauncher, _lockCleanup, _sessionLauncher, cardLogger,
                    _openMergeWizard, issueLinks,
                    _runningSessions, _windowActivator,
                    _displayNameStore, displayName,
                    _deletionService, _confirmDelete,
                    _starStore, isStarred,
                    onDeleted: RemoveCardAsync,
                    docFreshness: _docFreshness,
                    readmeService: _readmeService,
                    wrapUpStateStore: _wrapUpStateStore,
                    clipboardService: _clipboardService,
                    appSettings: _appSettings,
                    openEmbeddedTerminal: _openEmbeddedTerminal);
                if (newWrapUps.TryGetValue(session.Id, out var wrapTs))
                {
                    card.SetWrapUpRequestedAt(wrapTs);
                }
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
            // V1.4 (#112): starred sessions always pin to the top.
            var byStar = (b.IsStarred ? 1 : 0).CompareTo(a.IsStarred ? 1 : 0);
            if (byStar != 0)
            {
                return byStar;
            }

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
        var tokens = TokenizeSearch(SearchText);
        foreach (var card in Sessions)
        {
            if ((ShowInactive || IsActive(card.Status))
                && !_hiddenLabels.Contains(card.Label)
                && !_hiddenTiers.Contains(card.ModelTier)
                && IsDocFreshnessVisible(card.DocFreshness)
                && MatchesSearch(card, tokens))
            {
                VisibleSessions.Add(card);
            }
        }
    }

    private bool IsDocFreshnessVisible(DocFreshnessState state) =>
        !_hiddenDocFreshness.Contains(DocsFilterChip.ToBucket(state));

    private static string[] TokenizeSearch(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Array.Empty<string>();
        }
        return text.Split(
            new[] { ' ', '\t' },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static bool MatchesSearch(SessionCardViewModel card, string[] tokens)
    {
        if (tokens.Length == 0)
        {
            return true;
        }
        // Search both the user-visible DisplayName *and* the original Copilot
        // Title so renamed sessions still match queries against their original
        // summary. Both fields are short; we don't bother allocating a single
        // combined string.
        var display = card.DisplayName ?? string.Empty;
        var title = card.Title ?? string.Empty;
        foreach (var token in tokens)
        {
            if (display.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (title.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            return false;
        }
        return true;
    }

    /// <summary>
    /// Removes a card from the live <see cref="Sessions"/> collection
    /// immediately after a successful hard delete (#106) so the UI reflects
    /// the change without waiting for the next discovery refresh. Safe to
    /// call from any thread.
    /// </summary>
    private Task RemoveCardAsync(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        _dispatcher.Post(() =>
        {
            if (_byId.TryGetValue(sessionId, out var card))
            {
                if (ReferenceEquals(SelectedCard, card))
                {
                    SelectedCard = null;
                }
                Sessions.Remove(card);
                _byId.Remove(sessionId);
                RebuildVisible();
                OnPropertyChanged(nameof(TotalCount));
                OnPropertyChanged(nameof(ActiveCount));
                if (_tabClosedOnDelete is not null)
                {
                    try
                    {
                        _tabClosedOnDelete(sessionId);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Tab-close-on-delete callback threw for {SessionId}.", sessionId);
                    }
                }
            }
        });
        return Task.CompletedTask;
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
            OnPropertyChanged(nameof(LabelsFilterSummary));
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
            OnPropertyChanged(nameof(TiersFilterSummary));
        }
    }

    /// <summary>
    /// V1.3 (#148): toggles whether sessions whose Docs freshness falls in
    /// <paramref name="bucket"/> are visible. Used by
    /// <see cref="DocsFilterChip"/> bindings.
    /// </summary>
    internal void SetDocsFilterVisible(DocFreshnessFilterBucket bucket, bool isVisible)
    {
        var changed = isVisible ? _hiddenDocFreshness.Remove(bucket) : _hiddenDocFreshness.Add(bucket);
        if (changed)
        {
            RebuildVisible();
            OnPropertyChanged(nameof(DocsFilterSummary));
        }
    }

    /// <summary>
    /// V1.4 (#112): handles cross-component star changes (e.g. another
    /// surface stars a session). Updates the card and re-sorts so the
    /// pin animates to the top.
    /// </summary>
    private void OnStarStoreChanged(object? sender, SessionStarChangedEventArgs e)
    {
        _dispatcher.Post(() =>
        {
            if (_byId.TryGetValue(e.SessionId, out var card))
            {
                card.ApplyStarState(e.IsStarred);
                ResortInPlace();
                RebuildVisible();
            }
        });
    }

    /// <summary>
    /// V1.3 (#149): keeps each card in sync with the wrap-up state store
    /// so the badge animates in/out correctly when another surface
    /// records or clears a wrap-up timestamp.
    /// </summary>
    private void OnWrapUpStateStoreChanged(object? sender, WrapUpStateChangedEventArgs e)
    {
        _dispatcher.Post(() =>
        {
            if (_byId.TryGetValue(e.SessionId, out var card))
            {
                card.SetWrapUpRequestedAt(e.RequestedAt);
            }
        });
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
        Sessions.CollectionChanged -= OnSessionCardsCollectionChanged;
        foreach (var card in _crashBannerObservedCards)
        {
            card.PropertyChanged -= OnCrashBannerCardPropertyChanged;
        }
        _crashBannerObservedCards.Clear();
        if (_displayNameStore is not null)
        {
            _displayNameStore.DisplayNameChanged -= OnDisplayNameStoreChanged;
        }
        if (_starStore is not null)
        {
            _starStore.StarsChanged -= OnStarStoreChanged;
        }
        if (_wrapUpStateStore is not null)
        {
            _wrapUpStateStore.WrapUpStateChanged -= OnWrapUpStateStoreChanged;
        }
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

/// <summary>
/// V1.3 (#148): user-facing buckets for the Docs filter dropdown. Mirrors
/// <see cref="DocFreshnessState"/> but folds <c>Stale</c> + <c>VeryStale</c>
/// into a single <c>Stale</c> bucket because the column already shows them
/// with the same amber badge — splitting them in the filter would surprise
/// the user.
/// </summary>
public enum DocFreshnessFilterBucket
{
    Fresh = 0,
    Stale = 1,
    Missing = 2,
    NotApplicable = 3,
}

/// <summary>
/// Bindable toggle representing one <see cref="DocFreshnessFilterBucket"/>
/// in the dashboard Docs filter dropdown. Two-way bound to a CheckBox;
/// setting <see cref="IsVisible"/> calls back into
/// <see cref="SessionsViewModel.SetDocsFilterVisible"/>.
/// </summary>
public sealed partial class DocsFilterChip : ObservableObject
{
    private readonly SessionsViewModel _owner;

    [ObservableProperty]
    private bool _isVisible;

    public DocsFilterChip(DocFreshnessFilterBucket bucket, bool isVisible, SessionsViewModel owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        Bucket = bucket;
        _isVisible = isVisible;
        _owner = owner;
    }

    public DocFreshnessFilterBucket Bucket { get; }

    public string Label => Bucket switch
    {
        DocFreshnessFilterBucket.Fresh => "Fresh",
        DocFreshnessFilterBucket.Stale => "Stale",
        DocFreshnessFilterBucket.Missing => "Missing",
        DocFreshnessFilterBucket.NotApplicable => "N/A",
        _ => Bucket.ToString(),
    };

    /// <summary>
    /// Maps a raw <see cref="DocFreshnessState"/> to the user-facing bucket.
    /// <c>Stale</c> and <c>VeryStale</c> both fold into <c>Stale</c>.
    /// </summary>
    public static DocFreshnessFilterBucket ToBucket(DocFreshnessState state) => state switch
    {
        DocFreshnessState.Fresh => DocFreshnessFilterBucket.Fresh,
        DocFreshnessState.Stale => DocFreshnessFilterBucket.Stale,
        DocFreshnessState.VeryStale => DocFreshnessFilterBucket.Stale,
        DocFreshnessState.Missing => DocFreshnessFilterBucket.Missing,
        DocFreshnessState.NotApplicable => DocFreshnessFilterBucket.NotApplicable,
        _ => DocFreshnessFilterBucket.NotApplicable,
    };

    partial void OnIsVisibleChanged(bool value) => _owner.SetDocsFilterVisible(Bucket, value);
}
