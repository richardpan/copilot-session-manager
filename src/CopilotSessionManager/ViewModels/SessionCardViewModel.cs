using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CopilotSessionManager.Core.Cli;
using CopilotSessionManager.Core.Cost;
using CopilotSessionManager.Core.GitHub.Checks;
using CopilotSessionManager.Core.Models;
using CopilotSessionManager.Core.Sessions;
using CopilotSessionManager.Native;
using CopilotSessionManager.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CopilotSessionManager.ViewModels;

/// <summary>
/// Display projection over a single <see cref="Session"/>. Holds derived
/// strings + brushes so XAML stays declarative.
/// </summary>
public sealed partial class SessionCardViewModel : ObservableObject
{
    private Session _model;
    private SessionType _label;
    private PullRequestInfo? _liveOverridePullRequest;
    private bool _hasLiveOverride;
    private PullRequestCheckSummary? _liveOverrideChecks;
    private bool _hasChecksOverride;
    private readonly TimeProvider _timeProvider;
    private readonly IModelCatalog? _modelCatalog;
    private readonly IModelCostCalculator? _costCalculator;
    private readonly IFileLauncher? _fileLauncher;
    private readonly ISessionLockCleanup? _lockCleanup;
    private readonly ISessionLauncher? _sessionLauncher;
    private readonly IRunningSessionRegistry? _runningSessions;
    private readonly IWindowActivator? _windowActivator;
    private readonly ISessionDisplayNameStore? _displayNameStore;
    private readonly ISessionDeletionService? _deletionService;
    private readonly Func<SessionDeletionPrompt, bool>? _confirmDelete;
    private readonly Func<string, Task>? _onDeleted;
    private readonly ISessionStarStore? _starStore;
    private bool _isStarred;
    private readonly ILogger _logger;
    private string? _lastActionMessage;
    private string? _displayNameOverride;
    private bool _isEditingTitle;
    private string _editableTitle = string.Empty;
    private readonly Action<SessionCardViewModel>? _openMergeWizard;
    private IReadOnlyList<SubagentSummary> _subagents = Array.Empty<SubagentSummary>();
    private bool _subagentsLoaded;
    private Task? _subagentsLoadTask;
    private readonly IDocFreshnessService? _docFreshness;

    #region IssueLinks
    private readonly IssueLinksViewModel? _issueLinks;

    /// <summary>
    /// Optional per-card panel for manually linked GitHub issues (#70).
    /// Null when issues plumbing is not wired (e.g. unit-test ctors). Bound
    /// XAML hides the panel when this is null.
    /// </summary>
    public IssueLinksViewModel? IssueLinks => _issueLinks;

    /// <summary>True when the card has an issue-links view-model attached.</summary>
    public bool HasIssueLinks => _issueLinks is not null;
    #endregion

    public SessionCardViewModel(Session model)
        : this(model, SessionType.Exploratory, TimeProvider.System, modelCatalog: null, costCalculator: null, fileLauncher: null, lockCleanup: null, sessionLauncher: null, logger: null)
    {
    }

    public SessionCardViewModel(Session model, TimeProvider timeProvider)
        : this(model, SessionType.Exploratory, timeProvider, modelCatalog: null, costCalculator: null, fileLauncher: null, lockCleanup: null, sessionLauncher: null, logger: null)
    {
    }

    public SessionCardViewModel(Session model, SessionType label, TimeProvider timeProvider)
        : this(model, label, timeProvider, modelCatalog: null, costCalculator: null, fileLauncher: null, lockCleanup: null, sessionLauncher: null, logger: null)
    {
    }

    public SessionCardViewModel(
        Session model,
        SessionType label,
        TimeProvider timeProvider,
        IModelCatalog? modelCatalog,
        IModelCostCalculator? costCalculator)
        : this(model, label, timeProvider, modelCatalog, costCalculator, fileLauncher: null, lockCleanup: null, sessionLauncher: null, logger: null)
    {
    }

    public SessionCardViewModel(
        Session model,
        SessionType label,
        TimeProvider timeProvider,
        IModelCatalog? modelCatalog,
        IModelCostCalculator? costCalculator,
        IFileLauncher? fileLauncher)
        : this(model, label, timeProvider, modelCatalog, costCalculator, fileLauncher, lockCleanup: null, sessionLauncher: null, logger: null)
    {
    }

    public SessionCardViewModel(
        Session model,
        SessionType label,
        TimeProvider timeProvider,
        IModelCatalog? modelCatalog,
        IModelCostCalculator? costCalculator,
        IFileLauncher? fileLauncher,
        ISessionLockCleanup? lockCleanup,
        ISessionLauncher? sessionLauncher,
        ILogger? logger)
        : this(model, label, timeProvider, modelCatalog, costCalculator,
            fileLauncher, lockCleanup, sessionLauncher, logger,
            openMergeWizard: null, issueLinks: null)
    {
    }

    #region MergeWizard
    /// <summary>
    /// Constructor used by <see cref="SessionsViewModel"/> when a merge
    /// wizard callback is available. Mirrors the existing optional-callback
    /// pattern (#69 / #72): older call sites keep working via the chain
    /// above, while DI-aware call sites hand in
    /// <paramref name="openMergeWizard"/> so the card's
    /// <see cref="MergeIntoCommand"/> can pop the wizard for itself.
    /// </summary>
    public SessionCardViewModel(
        Session model,
        SessionType label,
        TimeProvider timeProvider,
        IModelCatalog? modelCatalog,
        IModelCostCalculator? costCalculator,
        IFileLauncher? fileLauncher,
        ISessionLockCleanup? lockCleanup,
        ISessionLauncher? sessionLauncher,
        ILogger? logger,
        Action<SessionCardViewModel>? openMergeWizard)
        : this(model, label, timeProvider, modelCatalog, costCalculator,
            fileLauncher, lockCleanup, sessionLauncher, logger,
            openMergeWizard, issueLinks: null)
    {
    }

    /// <summary>
    /// Canonical (DI-preferred) constructor. Adds the optional
    /// <paramref name="issueLinks"/> per-session panel (#70) on top of the
    /// merge-wizard plumbing. Both callbacks are independent — either may
    /// be null without affecting the other.
    /// </summary>
    public SessionCardViewModel(
        Session model,
        SessionType label,
        TimeProvider timeProvider,
        IModelCatalog? modelCatalog,
        IModelCostCalculator? costCalculator,
        IFileLauncher? fileLauncher,
        ISessionLockCleanup? lockCleanup,
        ISessionLauncher? sessionLauncher,
        ILogger? logger,
        Action<SessionCardViewModel>? openMergeWizard,
        IssueLinksViewModel? issueLinks)
        : this(model, label, timeProvider, modelCatalog, costCalculator,
            fileLauncher, lockCleanup, sessionLauncher, logger,
            openMergeWizard, issueLinks,
            runningSessions: null, windowActivator: null, displayNameStore: null,
            displayNameOverride: null,
            deletionService: null, confirmDelete: null, onDeleted: null)
    {
    }

    /// <summary>
    /// V1.1 canonical constructor adding the open / rename / delete plumbing
    /// (#104, #105, #106). All new parameters are optional so existing call
    /// sites (and tests) continue to compile via the chained ctor above.
    /// </summary>
    public SessionCardViewModel(
        Session model,
        SessionType label,
        TimeProvider timeProvider,
        IModelCatalog? modelCatalog,
        IModelCostCalculator? costCalculator,
        IFileLauncher? fileLauncher,
        ISessionLockCleanup? lockCleanup,
        ISessionLauncher? sessionLauncher,
        ILogger? logger,
        Action<SessionCardViewModel>? openMergeWizard,
        IssueLinksViewModel? issueLinks,
        IRunningSessionRegistry? runningSessions,
        IWindowActivator? windowActivator,
        ISessionDisplayNameStore? displayNameStore,
        string? displayNameOverride,
        ISessionDeletionService? deletionService,
        Func<SessionDeletionPrompt, bool>? confirmDelete,
        Func<string, Task>? onDeleted)
        : this(model, label, timeProvider, modelCatalog, costCalculator,
            fileLauncher, lockCleanup, sessionLauncher, logger,
            openMergeWizard, issueLinks,
            runningSessions, windowActivator, displayNameStore, displayNameOverride,
            deletionService, confirmDelete,
            starStore: null, isStarred: false,
            onDeleted: onDeleted)
    {
    }

    /// <summary>
    /// V1.4 canonical constructor adding the optional star plumbing
    /// (#112). All new parameters are optional so older call sites (and
    /// tests) compile unchanged via the chained ctor above.
    /// </summary>
    public SessionCardViewModel(
        Session model,
        SessionType label,
        TimeProvider timeProvider,
        IModelCatalog? modelCatalog,
        IModelCostCalculator? costCalculator,
        IFileLauncher? fileLauncher,
        ISessionLockCleanup? lockCleanup,
        ISessionLauncher? sessionLauncher,
        ILogger? logger,
        Action<SessionCardViewModel>? openMergeWizard,
        IssueLinksViewModel? issueLinks,
        IRunningSessionRegistry? runningSessions,
        IWindowActivator? windowActivator,
        ISessionDisplayNameStore? displayNameStore,
        string? displayNameOverride,
        ISessionDeletionService? deletionService,
        Func<SessionDeletionPrompt, bool>? confirmDelete,
        ISessionStarStore? starStore,
        bool isStarred,
        Func<string, Task>? onDeleted)
        : this(model, label, timeProvider, modelCatalog, costCalculator,
            fileLauncher, lockCleanup, sessionLauncher, logger,
            openMergeWizard, issueLinks,
            runningSessions, windowActivator, displayNameStore, displayNameOverride,
            deletionService, confirmDelete,
            starStore, isStarred,
            onDeleted: onDeleted,
            docFreshness: null)
    {
    }

    /// <summary>
    /// V1.3 (#147) canonical constructor adding the optional
    /// <see cref="IDocFreshnessService"/> for the SESSION-README/DOCS
    /// freshness badge. <paramref name="docFreshness"/> is nullable so
    /// existing call sites and tests keep compiling — when it's null the
    /// card reports <see cref="DocFreshnessState.NotApplicable"/>.
    /// </summary>
    public SessionCardViewModel(
        Session model,
        SessionType label,
        TimeProvider timeProvider,
        IModelCatalog? modelCatalog,
        IModelCostCalculator? costCalculator,
        IFileLauncher? fileLauncher,
        ISessionLockCleanup? lockCleanup,
        ISessionLauncher? sessionLauncher,
        ILogger? logger,
        Action<SessionCardViewModel>? openMergeWizard,
        IssueLinksViewModel? issueLinks,
        IRunningSessionRegistry? runningSessions,
        IWindowActivator? windowActivator,
        ISessionDisplayNameStore? displayNameStore,
        string? displayNameOverride,
        ISessionDeletionService? deletionService,
        Func<SessionDeletionPrompt, bool>? confirmDelete,
        ISessionStarStore? starStore,
        bool isStarred,
        Func<string, Task>? onDeleted,
        IDocFreshnessService? docFreshness)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _model = model;
        _label = label;
        _timeProvider = timeProvider;
        _modelCatalog = modelCatalog;
        _costCalculator = costCalculator;
        _fileLauncher = fileLauncher;
        _lockCleanup = lockCleanup;
        _sessionLauncher = sessionLauncher;
        _runningSessions = runningSessions;
        _windowActivator = windowActivator;
        _displayNameStore = displayNameStore;
        _displayNameOverride = NormaliseOverride(displayNameOverride);
        _deletionService = deletionService;
        _confirmDelete = confirmDelete;
        _onDeleted = onDeleted;
        _starStore = starStore;
        _isStarred = isStarred;
        _logger = logger ?? NullLogger.Instance;
        _openMergeWizard = openMergeWizard;
        _issueLinks = issueLinks;
        _docFreshness = docFreshness;

        OpenUrlCommand = new AsyncRelayCommand<string?>(OpenUrlAsync, CanOpenUrl);
        CleanupStaleLocksCommand = new AsyncRelayCommand(CleanupStaleLocksAsync, CanCleanupStaleLocks);
        ResumeCommand = new AsyncRelayCommand(ResumeAsync, CanResume);
        OpenCommand = new AsyncRelayCommand(OpenAsync, CanOpen);
        BeginRenameCommand = new RelayCommand(BeginRename, CanRename);
        CommitRenameCommand = new AsyncRelayCommand(CommitRenameAsync, () => _isEditingTitle);
        CancelRenameCommand = new RelayCommand(CancelRename, () => _isEditingTitle);
        DeleteCommand = new AsyncRelayCommand(DeleteAsync, CanDelete);
        ToggleStarCommand = new AsyncRelayCommand(ToggleStarAsync, () => _starStore is not null);
        MergeIntoCommand = new RelayCommand(InvokeMergeWizard, () => _openMergeWizard is not null);
    }

    /// <summary>
    /// Bound to the "Merge into…" right-click / toolbar action on a session
    /// card. Delegates to the host-supplied callback that owns wizard
    /// construction; <c>CanExecute</c> is false when no callback was wired
    /// (e.g. in tests that build the card directly).
    /// </summary>
    public IRelayCommand MergeIntoCommand { get; private set; } = new RelayCommand(static () => { }, static () => false);

    private void InvokeMergeWizard()
    {
        if (_openMergeWizard is null)
        {
            return;
        }
        try
        {
            _openMergeWizard(this);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open merge wizard for session {SessionId}.", _model.Id);
            LastActionMessage = $"Could not open merge wizard: {ex.Message}";
        }
    }
    #endregion

    public Session Model => _model;

    public string Id => _model.Id;

    public string ShortId => _model.Id.Length >= 8 ? _model.Id[..8] : _model.Id;

    /// <summary>
    /// V1.4 (#113): producer string read from the first session.start event,
    /// surfaced for filter chip grouping. <c>null</c> for sessions whose
    /// producer wasn't recorded (rendered as "(unknown)").
    /// </summary>
    public string? Producer => _model.Producer;

    /// <summary>
    /// V1.4 (#112): true when this session is starred (pinned to top of
    /// dashboard). Bound to the ★/☆ toggle on the card. Setting from XAML
    /// goes through <see cref="ToggleStarCommand"/>; this setter is private
    /// so changes always flow through the store.
    /// </summary>
    public bool IsStarred
    {
        get => _isStarred;
        private set => SetProperty(ref _isStarred, value);
    }

    /// <summary>
    /// Original Copilot-assigned title (summary, repository name, or short
    /// id). Always reflects the underlying model — use
    /// <see cref="DisplayName"/> to honour user renames (#105).
    /// </summary>
    public string Title =>
        !string.IsNullOrWhiteSpace(_model.Summary) ? _model.Summary!
        : !string.IsNullOrWhiteSpace(_model.Repository) ? _model.Repository!
        : ShortId;

    /// <summary>
    /// User-visible title. Returns the inline-rename override if set,
    /// otherwise falls back to <see cref="Title"/> (#105).
    /// </summary>
    public string DisplayName =>
        string.IsNullOrWhiteSpace(_displayNameOverride) ? Title : _displayNameOverride!;

    /// <summary>True when the user has set a display-name override.</summary>
    public bool HasDisplayNameOverride => !string.IsNullOrWhiteSpace(_displayNameOverride);

    /// <summary>
    /// Tooltip surfaced on the card title — shows the original Copilot name
    /// when an override is active so the user can still see the "real" name.
    /// </summary>
    public string TitleTooltip => HasDisplayNameOverride
        ? $"Renamed. Original: {Title}\nClick title to edit. Esc cancels."
        : "Click title to rename. Enter saves, Esc cancels.";

    /// <summary>
    /// True while the title is being edited inline. Bound by XAML to swap
    /// between a TextBlock and a TextBox.
    /// </summary>
    public bool IsEditingTitle
    {
        get => _isEditingTitle;
        private set
        {
            if (SetProperty(ref _isEditingTitle, value))
            {
                CommitRenameCommand.NotifyCanExecuteChanged();
                CancelRenameCommand.NotifyCanExecuteChanged();
            }
        }
    }

    /// <summary>Two-way bound buffer for the inline rename editor.</summary>
    public string EditableTitle
    {
        get => _editableTitle;
        set => SetProperty(ref _editableTitle, value ?? string.Empty);
    }

    public string? Repository => _model.Repository;

    public string? Branch => _model.Branch;

    public string? Cwd => _model.Cwd;

    public string? HostType => _model.HostType;

    public int TurnCount => _model.TurnCount;

    public SessionStatus Status => _model.Status;

    public SessionType Label => _label;

    public string LabelText => _label switch
    {
        SessionType.Exploratory => "Exploratory",
        SessionType.Research => "Research",
        SessionType.Feature => "Feature",
        SessionType.Bug => "Bug",
        SessionType.Refactor => "Refactor",
        SessionType.Docs => "Docs",
        SessionType.Infra => "Infra",
        SessionType.Experiment => "Experiment",
        _ => "Exploratory",
    };

    /// <summary>Color used for the label chip.</summary>
    public Brush LabelBrush => _label switch
    {
        SessionType.Exploratory => Brushes.MediumPurple,
        SessionType.Research => Brushes.MediumSlateBlue,
        SessionType.Feature => Brushes.SteelBlue,
        SessionType.Bug => Brushes.Crimson,
        SessionType.Refactor => Brushes.DarkCyan,
        SessionType.Docs => Brushes.DarkOliveGreen,
        SessionType.Infra => Brushes.SaddleBrown,
        SessionType.Experiment => Brushes.HotPink,
        _ => Brushes.Gray,
    };

    public string StatusLabel => _model.Status switch
    {
        SessionStatus.Working => "Working",
        SessionStatus.AwaitingApproval => "Awaiting approval",
        SessionStatus.AwaitingInput => "Awaiting input",
        SessionStatus.Idle => "Idle",
        SessionStatus.Inactive => "Inactive",
        SessionStatus.Orphaned => "Crashed",
        _ => "Unknown",
    };

    /// <summary>
    /// Glyph paired with <see cref="StatusBrush"/> so colour-blind users have
    /// a non-colour signal for session status. Rendered in the status pill
    /// alongside <see cref="StatusLabel"/>.
    /// </summary>
    public string StatusGlyph => _model.Status switch
    {
        SessionStatus.Working => "▶",
        SessionStatus.AwaitingApproval => "⚠",
        SessionStatus.AwaitingInput => "✎",
        SessionStatus.Idle => "◌",
        SessionStatus.Inactive => "·",
        SessionStatus.Orphaned => "✗",
        _ => "?",
    };

    /// <summary>
    /// Composite text for the status pill: glyph + label so the colour
    /// channel is not the only signal of state.
    /// </summary>
    public string StatusBadgeText => $"{StatusGlyph} {StatusLabel}";

    /// <summary>
    /// Single-sentence screen-reader label for the whole card. Bound to
    /// <c>AutomationProperties.Name</c> on the card root so Narrator
    /// announces something coherent when the user lands on a card.
    /// </summary>
    public string AutomationName
    {
        get
        {
            var repo = string.IsNullOrWhiteSpace(_model.Repository) ? "no repo" : _model.Repository!;
            var branch = string.IsNullOrWhiteSpace(_model.Branch) ? "no branch" : _model.Branch!;
            var updated = UpdatedRelative;
            return $"{LabelText} session: {DisplayName}. Status {StatusLabel}. Repository {repo}, branch {branch}. Updated {updated}.";
        }
    }

    /// <summary>True when the session is a crashed (orphaned) session that
    /// has stale lock files left behind by a dead process.</summary>
    public bool IsCrashed => _model.Status == SessionStatus.Orphaned;

    /// <summary>Color used for the status pill / left edge accent.</summary>
    public Brush StatusBrush => _model.Status switch
    {
        // Working: vibrant green; AwaitingApproval: amber; AwaitingInput: blue;
        // Idle: muted gray; Inactive: dim slate; Orphaned: red.
        SessionStatus.Working => Brushes.MediumSeaGreen,
        SessionStatus.AwaitingApproval => Brushes.Goldenrod,
        SessionStatus.AwaitingInput => Brushes.CornflowerBlue,
        SessionStatus.Idle => Brushes.DarkGray,
        SessionStatus.Inactive => Brushes.SlateGray,
        SessionStatus.Orphaned => Brushes.IndianRed,
        _ => Brushes.Gray,
    };

    public string UpdatedRelative => FormatRelative(_model.UpdatedAt);

    public string Age => FormatDuration(_timeProvider.GetUtcNow() - _model.CreatedAt);

    public string LockSummary => _model.Locks.Count == 0
        ? "no locks"
        : _model.Locks.Count == 1
            ? $"PID {_model.Locks[0].ProcessId}{(_model.Locks[0].IsAlive ? "" : " (dead)")}"
            : $"{_model.Locks.Count} locks";

    /// <summary>
    /// Resolved model record. <c>null</c> when no model info exists or the
    /// id is not in the embedded catalog.
    /// </summary>
    private CopilotModel? ResolvedModel
    {
        get
        {
            var id = _model.ModelInfo?.CurrentModelId;
            return id is null || _modelCatalog is null ? null : _modelCatalog.Resolve(id);
        }
    }

    /// <summary>
    /// Tier of the resolved model. <see cref="ModelTier.Unknown"/> when the
    /// model is missing or unknown to the catalog.
    /// </summary>
    public ModelTier ModelTier => ResolvedModel?.Tier ?? ModelTier.Unknown;

    /// <summary>Short text shown inside the model badge on the card.</summary>
    public string ModelDisplay
    {
        get
        {
            var resolved = ResolvedModel;
            if (resolved is not null)
            {
                return resolved.DisplayName;
            }
            var rawId = _model.ModelInfo?.CurrentModelId;
            return string.IsNullOrWhiteSpace(rawId) ? "Model unknown" : rawId!;
        }
    }

    /// <summary>Brush used for the model tier chip.</summary>
    public Brush ModelTierBrush => ModelTier switch
    {
        ModelTier.Premium => new SolidColorBrush(Color.FromRgb(0xF3, 0x8B, 0xA8)), // pink
        ModelTier.Standard => new SolidColorBrush(Color.FromRgb(0x89, 0xB4, 0xFA)), // blue
        ModelTier.Fast => new SolidColorBrush(Color.FromRgb(0xA6, 0xE3, 0xA1)), // green
        _ => new SolidColorBrush(Color.FromRgb(0x7F, 0x84, 0x9C)), // gray
    };

    /// <summary>
    /// USD cost estimate. Returns "—" for active sessions (no shutdown event)
    /// or when the model info is missing. Otherwise formats as currency
    /// (US locale) and prefixes "~" when any portion is from an unknown model.
    /// </summary>
    public string CostDisplay
    {
        get
        {
            var result = _costCalculator?.Estimate(_model.ModelInfo);
            if (result is null)
            {
                return "—";
            }
            var formatted = result.UsdAmount.ToString("C2", CultureInfo.GetCultureInfo("en-US"));
            return result.HasUnknownModels ? $"~{formatted}" : formatted;
        }
    }

    public IReadOnlyList<SubagentSummary> Subagents => _subagents;

    public bool HasSubagents => _subagents.Count > 0;

    public int SubagentCount => _subagents.Count;

    public long SubagentTokensTotal => _subagents.Sum(static s => s.TokensTotal);

    public string SubagentBadgeText => HasSubagents ? $"🧰 ×{SubagentCount}" : string.Empty;

    public string SubagentTokensDisplay => FormatTokens(SubagentTokensTotal);

    /// <summary>
    /// V1.3 (#147): Cached freshness result for the session's docs. Recomputed
    /// every read since the underlying file mtime may change at any time;
    /// the call is cheap (single <c>File.Exists</c> + <c>GetLastWriteTimeUtc</c>).
    /// Null service falls back to <see cref="DocFreshnessState.NotApplicable"/>.
    /// </summary>
    private DocFreshnessResult ComputeDocFreshness() =>
        _docFreshness?.Evaluate(_model.Id, _model.CreatedAt)
            ?? new DocFreshnessResult(DocFreshnessState.NotApplicable, null);

    /// <summary>Traffic-light state for the SESSION-README/DOCS freshness badge.</summary>
    public DocFreshnessState DocFreshness => ComputeDocFreshness().State;

    /// <summary>
    /// Sort key for the "Docs" data-grid column. Ordered so that VeryStale
    /// → Stale → Missing surface to the top of an ascending sort, with
    /// Fresh and NotApplicable rows at the bottom.
    /// </summary>
    public int DocFreshnessSortKey => DocFreshness switch
    {
        DocFreshnessState.VeryStale => 0,
        DocFreshnessState.Stale => 1,
        DocFreshnessState.Missing => 2,
        DocFreshnessState.Fresh => 3,
        DocFreshnessState.NotApplicable => 4,
        _ => 5,
    };

    /// <summary>Caption shown inside the "Docs" badge cell.</summary>
    public string DocFreshnessCaption
    {
        get
        {
            var (state, ageDays) = ComputeDocFreshness();
            return state switch
            {
                DocFreshnessState.Fresh => "📄 ✓ fresh",
                DocFreshnessState.Stale => $"📄 ⚠ stale {ageDays ?? 0}d",
                DocFreshnessState.VeryStale => $"📄 ⚠ stale {ageDays ?? 0}d",
                DocFreshnessState.Missing => "📄 ✗ missing",
                DocFreshnessState.NotApplicable => "📄 — n/a",
                _ => string.Empty,
            };
        }
    }

    /// <summary>Tooltip on the "Docs" badge cell.</summary>
    public string DocFreshnessTooltip => DocFreshness switch
    {
        DocFreshnessState.Fresh =>
            "SESSION-README is up to date (updated within the last day). Click to open.",
        DocFreshnessState.Stale =>
            "SESSION-README is between 1 and 7 days old. Click to regenerate and open.",
        DocFreshnessState.VeryStale =>
            "SESSION-README is more than 7 days old. Click to regenerate and open.",
        DocFreshnessState.Missing =>
            "No SESSION-README has been generated yet. Click to scaffold and open.",
        DocFreshnessState.NotApplicable =>
            "Session is too new for a freshness check (under 30 minutes old).",
        _ => string.Empty,
    };

    /// <summary>
    /// Total tokens consumed across all models in this session, formatted
    /// for at-a-glance display. Returns "—" when no usage data is available
    /// (active sessions before shutdown, or sessions without model events).
    /// Format: ``999`` (raw) → ``1.2k`` (1k–9.9k) → ``12k`` (10k–999k) → ``1.2M`` (≥ 1M).
    /// </summary>
    public string TokensDisplay
    {
        get
        {
            var own = OwnTokensRaw;
            var display = FormatTokens(own);
            var subagentTokens = SubagentTokensTotal;
            return subagentTokens > 0
                ? $"{display} (+{FormatTokens(subagentTokens)})"
                : display;
        }
    }

    /// <summary>Absolute token count for the session plus completed sub-agents.</summary>
    public long TotalTokensRaw => OwnTokensRaw + SubagentTokensTotal;

    /// <summary>Tooltip on the Tokens column — exact number + provenance hint.</summary>
    public string TokensTooltip
    {
        get
        {
            var info = _model.ModelInfo;
            if ((info?.UsageByModel is null || info.UsageByModel.Count == 0 || OwnTokensRaw == 0) && !HasSubagents)
            {
                return "Tokens not yet recorded — only available after the session ends.";
            }

            var lines = new List<string>();
            if (info?.UsageByModel is not null && info.UsageByModel.Count > 0 && OwnTokensRaw > 0)
            {
                var formatted = OwnTokensRaw.ToString("N0", CultureInfo.GetCultureInfo("en-US"));
                var modelCount = info.UsageByModel.Count;
                var noun = modelCount == 1 ? "model" : "models";
                lines.Add($"{formatted} tokens consumed across {modelCount} {noun}.");
                lines.Add(info.IsFromShutdown
                    ? "Source: session shutdown record (final)."
                    : "Source: live snapshot — total may grow.");
            }
            else
            {
                lines.Add("Parent session tokens not yet recorded.");
            }

            if (HasSubagents)
            {
                var count = SubagentCount;
                var noun = count == 1 ? "sub-agent" : "sub-agents";
                var avg = count == 0 ? 0 : SubagentTokensTotal / count;
                lines.Add($"+ {count} {noun} totalling {FormatTokens(SubagentTokensTotal)} tokens ({FormatTokens(avg)} avg)");
            }

            return string.Join("\n", lines);
        }
    }

    private long OwnTokensRaw
    {
        get
        {
            var info = _model.ModelInfo;
            if (info?.UsageByModel is null || info.UsageByModel.Count == 0)
            {
                return 0;
            }
            long sum = 0;
            foreach (var usage in info.UsageByModel.Values)
            {
                sum += usage.TotalTokens;
            }
            return sum;
        }
    }

    /// <summary>Tooltip shown on the model badge.</summary>
    public string ModelTooltip
    {
        get
        {
            var info = _model.ModelInfo;
            if (info is null || string.IsNullOrWhiteSpace(info.CurrentModelId))
            {
                return "Model unknown — no model events recorded for this session yet.";
            }

            var resolved = ResolvedModel;
            var name = resolved?.DisplayName ?? info.CurrentModelId;
            var tier = resolved?.Tier.ToString() ?? "Unknown";
            var sourceLine = info.IsFromShutdown
                ? "Source: session shutdown record (final)."
                : "Source: most recent tool execution (live).";
            var costLine = info.IsFromShutdown
                ? "Cost: estimated, based on default per-million rates."
                : "Cost: only available after the session ends.";
            return $"Model: {name}\nTier: {tier}\n{sourceLine}\n{costLine}";
        }
    }

    /// <summary>
    /// Effective <see cref="PullRequestInfo"/> for this card. The live value
    /// pushed via <see cref="SetPullRequest"/> wins; otherwise we fall back
    /// to whatever the resolver attached to the session model (currently
    /// always null but reserved for future persistence).
    /// </summary>
    public PullRequestInfo? PullRequest
        => _hasLiveOverride ? _liveOverridePullRequest : _model.GitHubLinks?.PullRequest;

    /// <summary>Web URL for the GitHub repository, or null when unknown.</summary>
    public string? RepositoryUrl => _model.GitHubLinks?.RepositoryUrl;

    /// <summary>Web URL for the working branch on GitHub, or null when unknown.</summary>
    public string? BranchUrl => _model.GitHubLinks?.BranchUrl;

    /// <summary>True when <see cref="RepositoryUrl"/> is present.</summary>
    public bool HasRepositoryUrl => !string.IsNullOrEmpty(RepositoryUrl);

    /// <summary>True when <see cref="BranchUrl"/> is present.</summary>
    public bool HasBranchUrl => !string.IsNullOrEmpty(BranchUrl);

    /// <summary>True when a PR has been resolved for this session's branch.</summary>
    public bool HasPullRequest => PullRequest is not null;

    /// <summary>PR number (e.g. 42), or null when no PR is known.</summary>
    public int? PullRequestNumber => PullRequest?.Number;

    /// <summary>Web URL of the PR, or null.</summary>
    public string? PullRequestUrl => PullRequest?.Url;

    /// <summary>Short label rendered inside the PR badge ("#42").</summary>
    public string PullRequestBadgeText => PullRequest is null ? string.Empty : $"#{PullRequest.Number}";

    /// <summary>Human label for the PR state (Open, Draft, Merged, Closed).</summary>
    public string PullRequestStateLabel => PullRequest?.State switch
    {
        PullRequestState.Open => "Open",
        PullRequestState.Draft => "Draft",
        PullRequestState.Merged => "Merged",
        PullRequestState.Closed => "Closed",
        _ => string.Empty,
    };

    /// <summary>Tooltip text for the PR badge.</summary>
    public string PullRequestTooltip => PullRequest is null
        ? string.Empty
        : $"PR #{PullRequest.Number} — {PullRequestStateLabel}\n{PullRequest.Title}";

    /// <summary>Brush used for the PR badge background, color-coded by state.</summary>
    public Brush PullRequestStateBrush => PullRequest?.State switch
    {
        // Catppuccin-ish palette consistent with model tier chips.
        PullRequestState.Open => new SolidColorBrush(Color.FromRgb(0xA6, 0xE3, 0xA1)), // green
        PullRequestState.Draft => new SolidColorBrush(Color.FromRgb(0x7F, 0x84, 0x9C)), // gray
        PullRequestState.Merged => new SolidColorBrush(Color.FromRgb(0xCB, 0xA6, 0xF7)), // purple
        PullRequestState.Closed => new SolidColorBrush(Color.FromRgb(0xF3, 0x8B, 0xA8)), // pink/red
        _ => new SolidColorBrush(Color.FromRgb(0x7F, 0x84, 0x9C)),
    };

    /// <summary>
    /// Opens a URL in the OS default browser. Bound to repo/branch/PR
    /// click handlers in XAML.
    /// </summary>
    public ICommand OpenUrlCommand { get; }

    /// <summary>
    /// Removes stale <c>inuse.*.lock</c> files for this session. Visible only
    /// when <see cref="IsCrashed"/> is true. Posts a status message after
    /// running.
    /// </summary>
    public IAsyncRelayCommand CleanupStaleLocksCommand { get; }

    /// <summary>
    /// Spawns an external PowerShell window running
    /// <c>copilot --resume &lt;id&gt;</c>. Cleans up stale lock files first.
    /// Visible only when <see cref="IsCrashed"/> is true.
    /// </summary>
    public IAsyncRelayCommand ResumeCommand { get; }

    /// <summary>
    /// Always-visible launch action (#104): activates an existing tracked
    /// PowerShell window for this session if one is alive, otherwise spawns
    /// a new one via <see cref="ISessionLauncher"/>. Disabled in unit tests
    /// that don't wire a launcher.
    /// </summary>
    public IAsyncRelayCommand OpenCommand { get; private set; } = new AsyncRelayCommand(static () => Task.CompletedTask, static () => false);

    /// <summary>
    /// Begins inline editing of the card title (#105). No-op when no
    /// display-name store is wired or when already editing.
    /// </summary>
    public IRelayCommand BeginRenameCommand { get; private set; } = new RelayCommand(static () => { }, static () => false);

    /// <summary>Commits the inline-rename buffer to the display-name store (#105).</summary>
    public IAsyncRelayCommand CommitRenameCommand { get; private set; } = new AsyncRelayCommand(static () => Task.CompletedTask, static () => false);

    /// <summary>Cancels inline editing without saving (#105).</summary>
    public IRelayCommand CancelRenameCommand { get; private set; } = new RelayCommand(static () => { }, static () => false);

    /// <summary>
    /// Hard-deletes the session from disk after the host's confirm callback
    /// returns true (#106). Disabled in unit tests that don't wire a deletion
    /// service.
    /// </summary>
    public IAsyncRelayCommand DeleteCommand { get; private set; } = new AsyncRelayCommand(static () => Task.CompletedTask, static () => false);

    /// <summary>
    /// V1.4 (#112): toggles star state. Disabled in unit-test contexts that
    /// don't wire an <see cref="ISessionStarStore"/>.
    /// </summary>
    public IAsyncRelayCommand ToggleStarCommand { get; private set; } = new AsyncRelayCommand(static () => Task.CompletedTask, static () => false);

    /// <summary>
    /// V1.4 (#112): tooltip surfaced on the ★/☆ toggle.
    /// </summary>
    public string StarTooltip => IsStarred
        ? "Unstar (remove pin)"
        : "Star (pin to top)";

    /// <summary>
    /// V1.4 (#112): persists the new star state, then optimistically updates
    /// the card. The store fires <see cref="ISessionStarStore.StarsChanged"/>
    /// which the dashboard view-model uses to re-sort.
    /// </summary>
    private async Task ToggleStarAsync()
    {
        if (_starStore is null)
        {
            return;
        }
        try
        {
            if (IsStarred)
            {
                await _starStore.RemoveAsync(_model.Id, CancellationToken.None).ConfigureAwait(true);
            }
            else
            {
                await _starStore.SetAsync(_model.Id, CancellationToken.None).ConfigureAwait(true);
            }
            // Reflect immediately even if the store event hasn't fanned back yet.
            ApplyStarState(!IsStarred);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to toggle star for {SessionId}.", _model.Id);
            LastActionMessage = $"Could not update star: {ex.Message}";
        }
    }

    /// <summary>
    /// V1.4 (#112): cross-component star update path — the dashboard
    /// view-model calls this when <see cref="ISessionStarStore.StarsChanged"/>
    /// fires for this session id.
    /// </summary>
    internal void ApplyStarState(bool isStarred)
    {
        if (_isStarred == isStarred)
        {
            return;
        }
        IsStarred = isStarred;
        OnPropertyChanged(nameof(StarTooltip));
    }

    /// <summary>
    /// Last result string from a card-level action (cleanup or resume), or
    /// null if no action has been invoked yet. Bound by tests; XAML can
    /// surface it via tooltip if desired.
    /// </summary>
    public string? LastActionMessage
    {
        get => _lastActionMessage;
        private set => SetProperty(ref _lastActionMessage, value);
    }

    private bool CanCleanupStaleLocks() => IsCrashed && _lockCleanup is not null;

    private async Task CleanupStaleLocksAsync()
    {
        if (_lockCleanup is null)
        {
            return;
        }
        try
        {
            var removed = await _lockCleanup.CleanupAsync(_model.Id).ConfigureAwait(true);
            LastActionMessage = removed == 0
                ? "No stale locks to remove."
                : removed == 1
                    ? "Removed 1 stale lock."
                    : $"Removed {removed} stale locks.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to clean up stale locks for {SessionId}.", _model.Id);
            LastActionMessage = $"Cleanup failed: {ex.Message}";
        }
    }

    private bool CanResume() => IsCrashed && _sessionLauncher is not null;

    private async Task ResumeAsync()
    {
        if (_sessionLauncher is null)
        {
            return;
        }
        try
        {
            // Best-effort: nuke stale locks first so the resumed CLI doesn't
            // trip over them. Failures here don't block the launch.
            if (_lockCleanup is not null)
            {
                try
                { await _lockCleanup.CleanupAsync(_model.Id).ConfigureAwait(true); }
                catch (Exception ex) { _logger.LogWarning(ex, "Pre-resume cleanup failed for {SessionId}.", _model.Id); }
            }

            var result = await _sessionLauncher.LaunchAsync(_model.Id, _model.Cwd).ConfigureAwait(true);
            if (result.ProcessId is int pid)
            {
                _runningSessions?.Register(_model.Id, pid);
            }
            LastActionMessage = $"Launched PowerShell (pid {result.ProcessId?.ToString() ?? "?"}).";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to resume session {SessionId}.", _model.Id);
            LastActionMessage = $"Resume failed: {ex.Message}";
        }
    }

    private bool CanOpen() => _sessionLauncher is not null;

    /// <summary>
    /// V1.1 launch flow (#104): if a previous launch's PID is still alive
    /// and has a top-level window, bring that window forward. Otherwise
    /// spawn a fresh <c>pwsh.exe</c> via the existing launcher and remember
    /// its PID for next time.
    /// </summary>
    private async Task OpenAsync()
    {
        if (_sessionLauncher is null)
        {
            return;
        }

        // Reuse path: try to bring the tracked window to the foreground.
        if (_runningSessions is not null
            && _windowActivator is not null
            && _runningSessions.TryGetProcessId(_model.Id) is int trackedPid)
        {
            var outcome = _windowActivator.Activate(trackedPid);
            switch (outcome)
            {
                case WindowActivationResult.Activated:
                    LastActionMessage = $"Brought existing PowerShell window forward (pid {trackedPid}).";
                    return;
                case WindowActivationResult.Win32Failure:
                    // Foreground was refused but the window flashed in the
                    // taskbar; treat as success rather than spawn a duplicate.
                    LastActionMessage = $"Existing window flashed (pid {trackedPid}). Click the taskbar to activate.";
                    return;
                case WindowActivationResult.NoMainWindow:
                    // Window not yet available. Best-effort: relaunch only if
                    // the tracked process truly looks dead; otherwise leave
                    // the user with a duplicate-suppression message.
                    LastActionMessage = $"PowerShell pid {trackedPid} has no window yet. Try again in a moment.";
                    return;
                case WindowActivationResult.ProcessNotRunning:
                default:
                    _runningSessions.Unregister(_model.Id);
                    break;
            }
        }

        // Fresh launch path.
        try
        {
            var result = await _sessionLauncher.LaunchAsync(_model.Id, _model.Cwd).ConfigureAwait(true);
            if (result.ProcessId is int pid)
            {
                _runningSessions?.Register(_model.Id, pid);
                LastActionMessage = $"Launched PowerShell (pid {pid}).";
            }
            else
            {
                LastActionMessage = "Launched PowerShell.";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open session {SessionId}.", _model.Id);
            LastActionMessage = $"Open failed: {ex.Message}";
        }
    }

    private bool CanRename() => _displayNameStore is not null;

    private void BeginRename()
    {
        if (_displayNameStore is null || _isEditingTitle)
        {
            return;
        }
        EditableTitle = DisplayName;
        IsEditingTitle = true;
    }

    private async Task CommitRenameAsync()
    {
        if (_displayNameStore is null || !_isEditingTitle)
        {
            return;
        }

        var trimmed = (_editableTitle ?? string.Empty).Trim();
        var clearing = string.IsNullOrEmpty(trimmed)
                       || string.Equals(trimmed, Title, StringComparison.Ordinal);

        try
        {
            if (clearing)
            {
                await _displayNameStore.RemoveAsync(_model.Id, CancellationToken.None).ConfigureAwait(true);
                ApplyDisplayNameOverride(null);
                LastActionMessage = "Reverted to original session name.";
            }
            else
            {
                await _displayNameStore.SetAsync(_model.Id, trimmed, CancellationToken.None).ConfigureAwait(true);
                ApplyDisplayNameOverride(trimmed);
                LastActionMessage = $"Renamed session to '{trimmed}'.";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save display-name override for {SessionId}.", _model.Id);
            LastActionMessage = $"Rename failed: {ex.Message}";
        }
        finally
        {
            IsEditingTitle = false;
        }
    }

    private void CancelRename()
    {
        if (!_isEditingTitle)
        {
            return;
        }
        EditableTitle = DisplayName;
        IsEditingTitle = false;
    }

    /// <summary>
    /// Updates the cached override (without writing to disk) and raises
    /// change notifications for the title-derived projections. Called by
    /// <see cref="SessionsViewModel"/> when the display-name store fires
    /// <c>DisplayNameChanged</c> from another component.
    /// </summary>
    public void ApplyDisplayNameOverride(string? overrideValue)
    {
        var normalised = NormaliseOverride(overrideValue);
        if (string.Equals(normalised, _displayNameOverride, StringComparison.Ordinal))
        {
            return;
        }
        _displayNameOverride = normalised;
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(HasDisplayNameOverride));
        OnPropertyChanged(nameof(TitleTooltip));
        OnPropertyChanged(nameof(AutomationName));
    }

    private static string? NormaliseOverride(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private bool CanDelete() => _deletionService is not null && _confirmDelete is not null;

    private async Task DeleteAsync()
    {
        if (_deletionService is null || _confirmDelete is null)
        {
            return;
        }

        var prompt = new SessionDeletionPrompt(_model.Id, DisplayName);
        bool confirmed;
        try
        {
            confirmed = _confirmDelete(prompt);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Delete-confirm callback threw for {SessionId}.", _model.Id);
            LastActionMessage = $"Delete cancelled (dialog error: {ex.Message}).";
            return;
        }
        if (!confirmed)
        {
            return;
        }

        try
        {
            var result = await _deletionService.DeleteAsync(_model.Id).ConfigureAwait(true);
            if (!result.Success)
            {
                LastActionMessage = result.ErrorMessage ?? "Delete failed.";
                return;
            }
            LastActionMessage = "Session deleted.";
            if (_onDeleted is not null)
            {
                try
                { await _onDeleted(_model.Id).ConfigureAwait(true); }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Post-delete callback threw for {SessionId}.", _model.Id);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete session {SessionId}.", _model.Id);
            LastActionMessage = $"Delete failed: {ex.Message}";
        }
    }

    /// <summary>
    /// Replaces the cached PR for this card with <paramref name="info"/>
    /// (or clears it when null). Raises change notifications for every
    /// PR-derived property so the badge updates without a full UpdateFrom.
    /// </summary>
    public void SetPullRequest(PullRequestInfo? info)
    {
        _hasLiveOverride = true;
        _liveOverridePullRequest = info;
        // A new PR resolution invalidates any previously-cached check
        // rollup — the next checks lookup will repopulate it.
        _hasChecksOverride = false;
        _liveOverrideChecks = null;
        RaisePullRequestChanged();
        RaiseChecksChanged();
    }

    /// <summary>
    /// Replaces the cached CI check rollup for this card. Pass <c>null</c>
    /// to clear (e.g. when no PR exists or the lookup failed). Raises
    /// change notifications for every check-derived property.
    /// </summary>
    public void SetChecks(PullRequestCheckSummary? summary)
    {
        _hasChecksOverride = true;
        _liveOverrideChecks = summary;
        RaiseChecksChanged();
    }

    /// <summary>
    /// Resolved CI rollup for this card's PR. <see cref="PullRequestCheckRollup.None"/>
    /// when there is no PR, no override has been pushed yet, or the
    /// lookup returned no checks.
    /// </summary>
    public PullRequestCheckRollup CheckRollup =>
        _hasChecksOverride ? (_liveOverrideChecks?.Rollup ?? PullRequestCheckRollup.None)
        : PullRequestCheckRollup.None;

    /// <summary>True when there's a meaningful CI status to render.</summary>
    public bool HasChecks => HasPullRequest && CheckRollup != PullRequestCheckRollup.None;

    /// <summary>Glyph rendered inside the CI badge.</summary>
    public string CheckBadgeText => CheckRollup switch
    {
        PullRequestCheckRollup.Success => "\u2713", // ✓
        PullRequestCheckRollup.Failure => "\u2717", // ✗
        PullRequestCheckRollup.Pending => "\u25CF", // ●
        _ => string.Empty,
    };

    /// <summary>Background brush for the CI badge — colour-coded by rollup.</summary>
    public Brush CheckBadgeBrush => CheckRollup switch
    {
        PullRequestCheckRollup.Success => new SolidColorBrush(Color.FromRgb(0xA6, 0xE3, 0xA1)), // green
        PullRequestCheckRollup.Failure => new SolidColorBrush(Color.FromRgb(0xF3, 0x8B, 0xA8)), // red/pink
        PullRequestCheckRollup.Pending => new SolidColorBrush(Color.FromRgb(0xF9, 0xE2, 0xAF)), // yellow
        _ => Brushes.Transparent,
    };

    /// <summary>Tooltip for the CI badge — lists failing/pending check names.</summary>
    public string CheckTooltip
    {
        get
        {
            var summary = _hasChecksOverride ? _liveOverrideChecks : null;
            if (summary is null || summary.Rollup == PullRequestCheckRollup.None)
            {
                return string.Empty;
            }

            var header = summary.Rollup switch
            {
                PullRequestCheckRollup.Success => "All checks passing",
                PullRequestCheckRollup.Failure => "Checks failing",
                PullRequestCheckRollup.Pending => "Checks running",
                _ => string.Empty,
            };

            if (summary.AttentionCheckNames.Count == 0)
            {
                return header;
            }
            return $"{header}\n  - {string.Join("\n  - ", summary.AttentionCheckNames)}";
        }
    }

    public void SetSubagents(IReadOnlyList<SubagentSummary> subagents)
    {
        _subagents = subagents ?? Array.Empty<SubagentSummary>();
        OnPropertyChanged(nameof(Subagents));
        OnPropertyChanged(nameof(HasSubagents));
        OnPropertyChanged(nameof(SubagentCount));
        OnPropertyChanged(nameof(SubagentTokensTotal));
        OnPropertyChanged(nameof(SubagentBadgeText));
        OnPropertyChanged(nameof(SubagentTokensDisplay));
        OnPropertyChanged(nameof(TokensDisplay));
        OnPropertyChanged(nameof(TotalTokensRaw));
        OnPropertyChanged(nameof(TokensTooltip));
    }

    public Task LoadSubagentsAsync(ISubagentScanService scanService, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scanService);
        if (_subagentsLoaded)
        {
            return Task.CompletedTask;
        }

        _subagentsLoadTask ??= LoadSubagentsCoreAsync(scanService, ct);
        return _subagentsLoadTask;
    }

    private async Task LoadSubagentsCoreAsync(ISubagentScanService scanService, CancellationToken ct)
    {
        try
        {
            var subagents = await scanService.ScanAsync(Id, ct);
            SetSubagents(subagents);
            _subagentsLoaded = true;
        }
        catch
        {
            _subagentsLoadTask = null;
            throw;
        }
    }

    private bool CanOpenUrl(string? url) => !string.IsNullOrWhiteSpace(url) && _fileLauncher is not null;

    private async Task OpenUrlAsync(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || _fileLauncher is null)
        {
            return;
        }
        await _fileLauncher.OpenAsync(url).ConfigureAwait(false);
    }

    private void RaisePullRequestChanged()
    {
        OnPropertyChanged(nameof(PullRequest));
        OnPropertyChanged(nameof(HasPullRequest));
        OnPropertyChanged(nameof(PullRequestNumber));
        OnPropertyChanged(nameof(PullRequestUrl));
        OnPropertyChanged(nameof(PullRequestBadgeText));
        OnPropertyChanged(nameof(PullRequestStateLabel));
        OnPropertyChanged(nameof(PullRequestTooltip));
        OnPropertyChanged(nameof(PullRequestStateBrush));
    }

    private void RaiseChecksChanged()
    {
        OnPropertyChanged(nameof(CheckRollup));
        OnPropertyChanged(nameof(HasChecks));
        OnPropertyChanged(nameof(CheckBadgeText));
        OnPropertyChanged(nameof(CheckBadgeBrush));
        OnPropertyChanged(nameof(CheckTooltip));
    }

    /// <summary>
    /// Replaces this card's underlying model and raises change notifications
    /// for every projected property. Used by <see cref="SessionsViewModel"/>
    /// when discovery reports an updated <see cref="Session"/> for the same id.
    /// </summary>
    public void UpdateFrom(Session newModel)
    {
        ArgumentNullException.ThrowIfNull(newModel);
        if (!string.Equals(_model.Id, newModel.Id, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Cannot replace session model with a different id.", nameof(newModel));
        }

        _model = newModel;
        // Discovery's snapshot is authoritative for repo/branch links; reset
        // any live-PR override so we re-fetch on next snapshot/refresh.
        _hasLiveOverride = false;
        _liveOverridePullRequest = null;
        _hasChecksOverride = false;
        _liveOverrideChecks = null;
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(HasDisplayNameOverride));
        OnPropertyChanged(nameof(TitleTooltip));
        OnPropertyChanged(nameof(Repository));
        OnPropertyChanged(nameof(Branch));
        OnPropertyChanged(nameof(Cwd));
        OnPropertyChanged(nameof(HostType));
        OnPropertyChanged(nameof(TurnCount));
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(StatusLabel));
        OnPropertyChanged(nameof(StatusBrush));
        OnPropertyChanged(nameof(StatusGlyph));
        OnPropertyChanged(nameof(StatusBadgeText));
        OnPropertyChanged(nameof(AutomationName));
        OnPropertyChanged(nameof(IsCrashed));
        CleanupStaleLocksCommand.NotifyCanExecuteChanged();
        ResumeCommand.NotifyCanExecuteChanged();
        OpenCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(UpdatedRelative));
        OnPropertyChanged(nameof(Age));
        OnPropertyChanged(nameof(DocFreshness));
        OnPropertyChanged(nameof(DocFreshnessSortKey));
        OnPropertyChanged(nameof(DocFreshnessCaption));
        OnPropertyChanged(nameof(DocFreshnessTooltip));
        OnPropertyChanged(nameof(LockSummary));
        OnPropertyChanged(nameof(ModelDisplay));
        OnPropertyChanged(nameof(ModelTier));
        OnPropertyChanged(nameof(ModelTierBrush));
        OnPropertyChanged(nameof(CostDisplay));
        OnPropertyChanged(nameof(TokensDisplay));
        OnPropertyChanged(nameof(TotalTokensRaw));
        OnPropertyChanged(nameof(TokensTooltip));
        OnPropertyChanged(nameof(ModelTooltip));
        OnPropertyChanged(nameof(RepositoryUrl));
        OnPropertyChanged(nameof(BranchUrl));
        OnPropertyChanged(nameof(HasRepositoryUrl));
        OnPropertyChanged(nameof(HasBranchUrl));
        RaisePullRequestChanged();
        RaiseChecksChanged();
    }

    /// <summary>
    /// Updates the user-assigned label and raises change notifications for
    /// the label-related projections.
    /// </summary>
    public void UpdateLabel(SessionType label)
    {
        if (_label == label)
        {
            return;
        }

        _label = label;
        OnPropertyChanged(nameof(Label));
        OnPropertyChanged(nameof(LabelText));
        OnPropertyChanged(nameof(LabelBrush));
        OnPropertyChanged(nameof(AutomationName));
    }

    private string FormatRelative(DateTimeOffset when)
    {
        if (when == DateTimeOffset.MinValue)
        {
            return "—";
        }

        var delta = _timeProvider.GetUtcNow() - when;
        if (delta < TimeSpan.Zero)
        {
            return "just now";
        }

        return delta.TotalSeconds < 60 ? "just now"
            : delta.TotalMinutes < 60 ? $"{(int)delta.TotalMinutes} min ago"
            : delta.TotalHours < 24 ? $"{(int)delta.TotalHours} hr ago"
            : $"{(int)delta.TotalDays} d ago";
    }

    private static string FormatDuration(TimeSpan span)
    {
        if (span < TimeSpan.Zero)
        {
            span = TimeSpan.Zero;
        }

        return span.TotalSeconds < 60 ? $"{(int)span.TotalSeconds}s"
            : span.TotalMinutes < 60 ? $"{(int)span.TotalMinutes}m"
            : span.TotalHours < 24 ? $"{span.Hours}h {span.Minutes}m"
            : $"{(int)span.TotalDays}d {span.Hours}h";
    }

    private static string FormatTokens(long total)
    {
        if (total <= 0)
        {
            return "—";
        }
        if (total < 1000)
        {
            return total.ToString(CultureInfo.InvariantCulture);
        }
        if (total < 10_000)
        {
            return (total / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + "k";
        }
        if (total < 1_000_000)
        {
            return (total / 1000).ToString(CultureInfo.InvariantCulture) + "k";
        }
        return (total / 1_000_000.0).ToString("0.0", CultureInfo.InvariantCulture) + "M";
    }
}

/// <summary>
/// Payload handed to the host-supplied delete-confirm callback (#106). The
/// callback returns <c>true</c> to proceed with the hard delete or
/// <c>false</c> to abort.
/// </summary>
public sealed record SessionDeletionPrompt(string SessionId, string DisplayName);
