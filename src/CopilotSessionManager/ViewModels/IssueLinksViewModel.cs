using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CopilotSessionManager.Core.GitHub.Issues;
using CopilotSessionManager.Core.GitHub.Storage;
using CopilotSessionManager.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CopilotSessionManager.ViewModels;

/// <summary>
/// Per-session collection of manually linked GitHub issues. Owns the
/// observable collection of <see cref="IssueLinkViewModel"/> rendered in
/// the dashboard card, the "+ Issue" command that prompts the user, and
/// the persistence + metadata-fetch wiring.
/// </summary>
/// <remarks>
/// Construction is testable: the dialog is delivered as a callback
/// (<c>Func&lt;string?, IssueRef?&gt;</c>) so unit tests can inject a stub
/// without mounting any WPF window. Metadata fetches are best-effort and
/// fail closed: when the gh CLI is unavailable, the badge still appears
/// with a placeholder colour and the canonical URL.
/// </remarks>
public sealed partial class IssueLinksViewModel : ObservableObject, IDisposable
{
    private readonly string _sessionId;
    private readonly string? _defaultOwnerRepo;
    private readonly IGitHubIssuesClient? _issuesClient;
    private readonly ISessionGitHubLinksStore? _linksStore;
    private readonly IFileLauncher? _fileLauncher;
    private readonly IUiDispatcher _dispatcher;
    private readonly Func<string?, IssueRef?> _showAddDialog;
    private readonly ILogger _logger;
    private readonly IReadmeIssueRefProvider? _readmeRefs;

    // Debounce successive ReadmeChanged events for the same session — the
    // auto-readme service can fire several writes in quick succession during
    // a discovery cycle.
    private static readonly TimeSpan ReadmeRefreshDebounce = TimeSpan.FromSeconds(2);
    private DateTime _lastReadmeRefreshUtc = DateTime.MinValue;
    private readonly object _refreshLock = new();
    private bool _refreshScheduled;
    private bool _disposed;

    private string? _statusMessage;

    public IssueLinksViewModel(
        string sessionId,
        string? defaultOwnerRepo,
        IGitHubIssuesClient? issuesClient,
        ISessionGitHubLinksStore? linksStore,
        IFileLauncher? fileLauncher,
        IUiDispatcher dispatcher,
        Func<string?, IssueRef?> showAddDialog,
        ILogger<IssueLinksViewModel>? logger = null)
        : this(sessionId, defaultOwnerRepo, issuesClient, linksStore, fileLauncher,
            dispatcher, showAddDialog, logger, readmeRefs: null)
    {
    }

    /// <summary>
    /// DI-preferred constructor. Adds <see cref="IReadmeIssueRefProvider"/>
    /// (#71) so refs mentioned in the auto-generated README appear as
    /// badges alongside the user's manual links.
    /// </summary>
    public IssueLinksViewModel(
        string sessionId,
        string? defaultOwnerRepo,
        IGitHubIssuesClient? issuesClient,
        ISessionGitHubLinksStore? linksStore,
        IFileLauncher? fileLauncher,
        IUiDispatcher dispatcher,
        Func<string?, IssueRef?> showAddDialog,
        ILogger<IssueLinksViewModel>? logger,
        IReadmeIssueRefProvider? readmeRefs)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(showAddDialog);

        _sessionId = sessionId;
        _defaultOwnerRepo = NormaliseDefault(defaultOwnerRepo);
        _issuesClient = issuesClient;
        _linksStore = linksStore;
        _fileLauncher = fileLauncher;
        _dispatcher = dispatcher;
        _showAddDialog = showAddDialog;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _readmeRefs = readmeRefs;

        Links = new ObservableCollection<IssueLinkViewModel>();
        AddIssueCommand = new AsyncRelayCommand(AddIssueAsync);

        if (_readmeRefs is not null)
        {
            _readmeRefs.ReadmeChanged += OnReadmeChanged;
        }
    }

    /// <summary>Linked issues, in the order they were added.</summary>
    public ObservableCollection<IssueLinkViewModel> Links { get; }

    /// <summary>"+ Issue" — opens the add dialog and persists the chosen ref.</summary>
    public IAsyncRelayCommand AddIssueCommand { get; }

    /// <summary>Last user-facing status string from an add/remove operation.</summary>
    public string? StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    /// <summary>True when at least one issue is linked. Bound by tests.</summary>
    public bool HasLinks => Links.Count > 0;

    /// <summary>
    /// Loads previously persisted issue refs and starts an async fetch for
    /// each. Safe to call once per session — duplicates are ignored. Returns
    /// when the initial collection is populated; metadata loads continue in
    /// the background. Also folds in any refs parsed out of
    /// <c>SESSION-README.md</c> (#71); manual refs always win on dedup so a
    /// README mention of a manually-linked issue stays a single badge.
    /// </summary>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        await LoadManualRefsAsync(cancellationToken).ConfigureAwait(false);
        await LoadParsedRefsAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task LoadManualRefsAsync(CancellationToken cancellationToken)
    {
        if (_linksStore is null)
        {
            return;
        }

        SessionGitHubLinkOverrides? overrides;
        try
        {
            overrides = await _linksStore.GetAsync(_sessionId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to load issue refs for {SessionId}.", _sessionId);
            return;
        }

        if (overrides is null || overrides.IssueRefs.Count == 0)
        {
            return;
        }

        foreach (var canonical in overrides.IssueRefs)
        {
            if (!IssueRefParser.TryParse(canonical, defaultOwnerRepo: null, out var parsed))
            {
                continue;
            }

            _dispatcher.Post(() => AddBadge(parsed!, IssueLinkOrigin.Manual));
            QueueFetch(parsed!);
        }
    }

    private async Task LoadParsedRefsAsync(CancellationToken cancellationToken)
    {
        if (_readmeRefs is null)
        {
            return;
        }

        IReadOnlyList<IssueRef> parsed;
        try
        {
            parsed = await _readmeRefs.GetParsedRefsAsync(_sessionId, _defaultOwnerRepo, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to load parsed README refs for {SessionId}.", _sessionId);
            return;
        }

        if (parsed.Count == 0)
        {
            return;
        }

        foreach (var p in parsed)
        {
            _dispatcher.Post(() => AddBadge(p, IssueLinkOrigin.ParsedFromReadme));
            QueueFetch(p);
        }
    }

    /// <summary>
    /// Re-runs the README scan and reconciles the parsed badges in
    /// <see cref="Links"/>. Manual badges are never touched. Existing
    /// parsed badges that are no longer mentioned in the README are
    /// removed; new mentions are added.
    /// </summary>
    public async Task RefreshParsedRefsAsync(CancellationToken cancellationToken = default)
    {
        if (_readmeRefs is null)
        {
            return;
        }

        IReadOnlyList<IssueRef> parsed;
        try
        {
            parsed = await _readmeRefs.GetParsedRefsAsync(_sessionId, _defaultOwnerRepo, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to refresh parsed README refs for {SessionId}.", _sessionId);
            return;
        }

        var parsedSet = new HashSet<IssueRef>(parsed);

        _dispatcher.Post(() =>
        {
            // Drop parsed badges no longer mentioned.
            for (var i = Links.Count - 1; i >= 0; i--)
            {
                var existing = Links[i];
                if (existing.Origin == IssueLinkOrigin.ParsedFromReadme && !parsedSet.Contains(existing.Ref))
                {
                    Links.RemoveAt(i);
                }
            }

            foreach (var p in parsed)
            {
                if (Links.Any(l => l.Ref.Equals(p)))
                {
                    continue;
                }
                AddBadge(p, IssueLinkOrigin.ParsedFromReadme);
                QueueFetch(p);
            }

            OnPropertyChanged(nameof(HasLinks));
        });
    }

    private async Task AddIssueAsync()
    {
        IssueRef? chosen;
        try
        {
            chosen = _showAddDialog(_defaultOwnerRepo);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Add-issue dialog threw.");
            StatusMessage = $"Could not open dialog: {ex.Message}";
            return;
        }

        if (chosen is null)
        {
            return;
        }

        if (Links.Any(l => l.Ref.Equals(chosen)))
        {
            StatusMessage = $"Issue {chosen} is already linked.";
            return;
        }

        AddBadge(chosen, IssueLinkOrigin.Manual);

        if (_linksStore is not null)
        {
            try
            {
                await _linksStore.AddIssueRefAsync(_sessionId, chosen).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to persist issue {Ref} for {SessionId}.", chosen, _sessionId);
                StatusMessage = $"Could not save: {ex.Message}";
            }
        }

        QueueFetch(chosen);
        StatusMessage = $"Linked {chosen}.";
    }

    private async Task RemoveAsync(IssueRef issueRef)
    {
        ArgumentNullException.ThrowIfNull(issueRef);

        // Remove from the observable collection first so the UI updates
        // promptly even if the store is slow / fails.
        for (var i = Links.Count - 1; i >= 0; i--)
        {
            if (Links[i].Ref.Equals(issueRef))
            {
                Links.RemoveAt(i);
            }
        }
        OnPropertyChanged(nameof(HasLinks));

        if (_linksStore is null)
        {
            return;
        }

        try
        {
            await _linksStore.RemoveIssueRefAsync(_sessionId, issueRef).ConfigureAwait(false);
            StatusMessage = $"Removed {issueRef}.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to remove issue {Ref} for {SessionId}.", issueRef, _sessionId);
            StatusMessage = $"Could not remove: {ex.Message}";
        }
    }

    private void AddBadge(IssueRef issueRef, IssueLinkOrigin origin)
    {
        var existing = Links.FirstOrDefault(l => l.Ref.Equals(issueRef));
        if (existing is not null)
        {
            // Manual always wins — if a parsed ref arrives for a manually
            // linked issue, leave the existing manual badge alone.
            return;
        }

        var badge = new IssueLinkViewModel(
            issueRef,
            _defaultOwnerRepo ?? string.Empty,
            _fileLauncher,
            RemoveAsync,
            origin);
        Links.Add(badge);
        OnPropertyChanged(nameof(HasLinks));
    }

    private void OnReadmeChanged(object? sender, ReadmeIssueRefsChangedEventArgs e)
    {
        if (!string.Equals(e.SessionId, _sessionId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // Debounce: schedule one refresh per debounce window even if many
        // events arrive in quick succession.
        lock (_refreshLock)
        {
            if (_refreshScheduled)
            {
                return;
            }
            var sinceLast = DateTime.UtcNow - _lastReadmeRefreshUtc;
            var delay = sinceLast >= ReadmeRefreshDebounce
                ? TimeSpan.Zero
                : ReadmeRefreshDebounce - sinceLast;
            _refreshScheduled = true;
            _ = ScheduleRefreshAsync(delay);
        }
    }

    private async Task ScheduleRefreshAsync(TimeSpan delay)
    {
        try
        {
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay).ConfigureAwait(false);
            }
            await RefreshParsedRefsAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Debounced README refresh failed for {SessionId}.", _sessionId);
        }
        finally
        {
            lock (_refreshLock)
            {
                _refreshScheduled = false;
                _lastReadmeRefreshUtc = DateTime.UtcNow;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        if (_readmeRefs is not null)
        {
            _readmeRefs.ReadmeChanged -= OnReadmeChanged;
        }
    }

    private void QueueFetch(IssueRef issueRef)
    {
        if (_issuesClient is null)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var info = await _issuesClient.GetIssueAsync(issueRef).ConfigureAwait(false);
                if (info is null)
                {
                    return;
                }
                _dispatcher.Post(() =>
                {
                    var match = Links.FirstOrDefault(l => l.Ref.Equals(issueRef));
                    match?.ApplyInfo(info);
                });
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Issue metadata fetch failed for {Ref}.", issueRef);
            }
        });
    }

    private static string? NormaliseDefault(string? defaultOwnerRepo)
    {
        if (string.IsNullOrWhiteSpace(defaultOwnerRepo))
        {
            return null;
        }
        return defaultOwnerRepo.Trim().ToLowerInvariant();
    }
}
