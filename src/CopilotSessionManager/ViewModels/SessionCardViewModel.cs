using System;
using System.Globalization;
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
    private readonly ILogger _logger;
    private string? _lastActionMessage;

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
        _logger = logger ?? NullLogger.Instance;

        OpenUrlCommand = new AsyncRelayCommand<string?>(OpenUrlAsync, CanOpenUrl);
        CleanupStaleLocksCommand = new AsyncRelayCommand(CleanupStaleLocksAsync, CanCleanupStaleLocks);
        ResumeCommand = new AsyncRelayCommand(ResumeAsync, CanResume);
    }

    public Session Model => _model;

    public string Id => _model.Id;

    public string ShortId => _model.Id.Length >= 8 ? _model.Id[..8] : _model.Id;

    public string Title =>
        !string.IsNullOrWhiteSpace(_model.Summary) ? _model.Summary!
        : !string.IsNullOrWhiteSpace(_model.Repository) ? _model.Repository!
        : ShortId;

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
            LastActionMessage = $"Launched PowerShell (pid {result.ProcessId?.ToString() ?? "?"}).";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to resume session {SessionId}.", _model.Id);
            LastActionMessage = $"Resume failed: {ex.Message}";
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
        OnPropertyChanged(nameof(Repository));
        OnPropertyChanged(nameof(Branch));
        OnPropertyChanged(nameof(Cwd));
        OnPropertyChanged(nameof(HostType));
        OnPropertyChanged(nameof(TurnCount));
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(StatusLabel));
        OnPropertyChanged(nameof(StatusBrush));
        OnPropertyChanged(nameof(IsCrashed));
        CleanupStaleLocksCommand.NotifyCanExecuteChanged();
        ResumeCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(UpdatedRelative));
        OnPropertyChanged(nameof(Age));
        OnPropertyChanged(nameof(LockSummary));
        OnPropertyChanged(nameof(ModelDisplay));
        OnPropertyChanged(nameof(ModelTier));
        OnPropertyChanged(nameof(ModelTierBrush));
        OnPropertyChanged(nameof(CostDisplay));
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
}
