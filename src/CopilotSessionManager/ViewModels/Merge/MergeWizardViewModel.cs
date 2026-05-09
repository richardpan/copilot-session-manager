using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CopilotSessionManager.Core.Cli.Share;
using CopilotSessionManager.Core.Merge;
using CopilotSessionManager.Services;
using CopilotSessionManager.ViewModels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CopilotSessionManager.ViewModels.Merge;

/// <summary>
/// Wizard steps surfaced by <see cref="MergeWizardViewModel.CurrentStep"/>.
/// </summary>
public enum MergeWizardStep
{
    /// <summary>Pick a target session from the list of other candidates.</summary>
    PickTarget,

    /// <summary>Show the markdown preview from <c>copilot --share</c> and
    /// confirm the merge.</summary>
    PreviewAndConfirm,

    /// <summary>Long-running export+import is in progress.</summary>
    Running,

    /// <summary>Terminal state — success or error.</summary>
    Done,
}

/// <summary>
/// Top-level state machine behind the merge wizard window. Owns the
/// pipeline:
/// <list type="number">
///   <item>User picks a target → we kick off
///     <see cref="ICopilotShareInvoker.ExportAsync"/>.</item>
///   <item>Markdown preview is rendered and the user confirms.</item>
///   <item><see cref="ISessionMerger.MergeAsync"/> writes the import +
///     appends the README log entry.</item>
///   <item>Success / failure is reported in the Done step.</item>
/// </list>
/// </summary>
/// <remarks>
/// All state mutations are marshalled to the UI thread via
/// <see cref="IUiDispatcher"/> so binding updates fire on the correct
/// thread regardless of which task continuation produced them.
/// </remarks>
public sealed partial class MergeWizardViewModel : ObservableObject
{
    private readonly ICopilotShareInvoker _share;
    private readonly ISessionMerger _merger;
    private readonly IUiDispatcher _dispatcher;
    private readonly IFileLauncher? _fileLauncher;
    private readonly ILogger<MergeWizardViewModel> _logger;
    private readonly Action<SessionCardViewModel>? _onMergeComplete;
    private string? _mergedFilePath;
    private string? _resultingMergeNote;

    [ObservableProperty]
    private MergeWizardStep _currentStep = MergeWizardStep.PickTarget;

    [ObservableProperty]
    private MergeTargetCandidateViewModel? _selectedTarget;

    [ObservableProperty]
    private string _markdownPreview = string.Empty;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private string _progressMessage = string.Empty;

    [ObservableProperty]
    private bool _showInactiveTargets;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private bool _isSuccess;

    public MergeWizardViewModel(
        SessionCardViewModel sourceCard,
        IReadOnlyList<SessionCardViewModel> allSessions,
        ISessionMerger merger,
        ICopilotShareInvoker share,
        IUiDispatcher dispatcher,
        IFileLauncher? fileLauncher = null,
        TimeProvider? clock = null,
        ILogger<MergeWizardViewModel>? logger = null,
        Action<SessionCardViewModel>? onMergeComplete = null)
    {
        ArgumentNullException.ThrowIfNull(sourceCard);
        ArgumentNullException.ThrowIfNull(allSessions);
        ArgumentNullException.ThrowIfNull(merger);
        ArgumentNullException.ThrowIfNull(share);
        ArgumentNullException.ThrowIfNull(dispatcher);

        SourceCard = sourceCard;
        _share = share;
        _merger = merger;
        _dispatcher = dispatcher;
        _fileLauncher = fileLauncher;
        _logger = logger ?? NullLogger<MergeWizardViewModel>.Instance;
        _onMergeComplete = onMergeComplete;

        var effectiveClock = clock ?? TimeProvider.System;

        AllCandidates = new ReadOnlyCollection<MergeTargetCandidateViewModel>(
            allSessions
                .Where(s => !string.Equals(s.Id, sourceCard.Id, StringComparison.OrdinalIgnoreCase))
                .Select(s => new MergeTargetCandidateViewModel(s, effectiveClock))
                .OrderByDescending(c => c.SortKey)
                .ToList());

        TargetCandidates = new ObservableCollection<MergeTargetCandidateViewModel>();
        ApplyTargetFilter();

        NextCommand = new AsyncRelayCommand(NextAsync, CanGoNext);
        BackCommand = new RelayCommand(GoBack, CanGoBack);
        CancelCommand = new RelayCommand(Cancel, () => !IsRunning);
        ConfirmMergeCommand = new AsyncRelayCommand(ConfirmAsync, CanConfirm);
        OpenMergedFileCommand = new AsyncRelayCommand(OpenMergedFileAsync, CanOpenMergedFile);
    }

    /// <summary>The session being merged from. Read-only display surface.</summary>
    public SessionCardViewModel SourceCard { get; }

    /// <summary>The full list of candidate targets (excludes the source).</summary>
    public IReadOnlyList<MergeTargetCandidateViewModel> AllCandidates { get; }

    /// <summary>Targets after the active-only + search filters.</summary>
    public ObservableCollection<MergeTargetCandidateViewModel> TargetCandidates { get; }

    /// <summary>True when no candidates exist at all (only the source session).</summary>
    public bool HasNoCandidates => AllCandidates.Count == 0;

    /// <summary>
    /// On success after <see cref="ConfirmMergeCommand"/> completes, holds the
    /// merge note string that was appended to the target README. Null on
    /// failure or when the README append failed (engine still reports success).
    /// </summary>
    public string? ResultingMergeNote => _resultingMergeNote;

    /// <summary>
    /// On success, the absolute path of the merge import file written into
    /// the target session folder. Null until <see cref="ConfirmAsync"/>
    /// completes successfully.
    /// </summary>
    public string? MergedFilePath => _mergedFilePath;

    /// <summary>
    /// User-facing summary shown in the Done step. Pulled from
    /// <see cref="ErrorMessage"/> on failure or a fixed string on success.
    /// </summary>
    public string DoneSummary
    {
        get
        {
            if (CurrentStep != MergeWizardStep.Done)
            {
                return string.Empty;
            }
            if (IsSuccess)
            {
                return $"Merge complete. Imported source session into {SourceTargetSummary()}.";
            }
            return ErrorMessage ?? "Merge failed.";
        }
    }

    /// <summary>Title of the source/target labelling shown across all steps.</summary>
    public string SourceTitle => SourceCard.Title;

    /// <summary>Convenience accessor for the source's short id.</summary>
    public string SourceShortId => SourceCard.ShortId;

    /// <summary>Title of the picked target (or empty before a pick).</summary>
    public string TargetTitle => SelectedTarget?.Title ?? string.Empty;

    /// <summary>Short id of the picked target (or empty before a pick).</summary>
    public string TargetShortId => SelectedTarget?.ShortId ?? string.Empty;

    public IAsyncRelayCommand NextCommand { get; }
    public IRelayCommand BackCommand { get; }
    public IRelayCommand CancelCommand { get; }
    public IAsyncRelayCommand ConfirmMergeCommand { get; }
    public IAsyncRelayCommand OpenMergedFileCommand { get; }

    /// <summary>
    /// Raised when the wizard wants its host window to close. The window
    /// subscribes and dispatches <c>Close()</c> on the UI thread.
    /// </summary>
    public event EventHandler? CloseRequested;

    partial void OnCurrentStepChanged(MergeWizardStep value)
    {
        OnPropertyChanged(nameof(DoneSummary));
        OnPropertyChanged(nameof(TargetTitle));
        OnPropertyChanged(nameof(TargetShortId));
        NextCommand.NotifyCanExecuteChanged();
        BackCommand.NotifyCanExecuteChanged();
        ConfirmMergeCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedTargetChanged(MergeTargetCandidateViewModel? oldValue, MergeTargetCandidateViewModel? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.IsSelected = false;
        }
        if (newValue is not null)
        {
            newValue.IsSelected = true;
        }
        OnPropertyChanged(nameof(TargetTitle));
        OnPropertyChanged(nameof(TargetShortId));
        NextCommand.NotifyCanExecuteChanged();
        ConfirmMergeCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsRunningChanged(bool value)
    {
        NextCommand.NotifyCanExecuteChanged();
        BackCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
        ConfirmMergeCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsSuccessChanged(bool value)
    {
        OnPropertyChanged(nameof(DoneSummary));
        OpenMergedFileCommand.NotifyCanExecuteChanged();
    }

    partial void OnErrorMessageChanged(string? value)
    {
        OnPropertyChanged(nameof(DoneSummary));
    }

    partial void OnShowInactiveTargetsChanged(bool value) => ApplyTargetFilter();

    partial void OnSearchTextChanged(string value) => ApplyTargetFilter();

    private void ApplyTargetFilter()
    {
        TargetCandidates.Clear();
        var search = SearchText?.Trim() ?? string.Empty;
        foreach (var candidate in AllCandidates)
        {
            if (!ShowInactiveTargets && !candidate.IsActive)
            {
                continue;
            }
            if (search.Length > 0 && !MatchesSearch(candidate, search))
            {
                continue;
            }
            TargetCandidates.Add(candidate);
        }

        // Drop a stale selection that has been filtered out so the user
        // can't proceed against a hidden row.
        if (SelectedTarget is not null && !TargetCandidates.Contains(SelectedTarget))
        {
            SelectedTarget = null;
        }
    }

    private static bool MatchesSearch(MergeTargetCandidateViewModel candidate, string search)
    {
        return Contains(candidate.Title, search)
            || Contains(candidate.ShortId, search)
            || Contains(candidate.Subtitle, search);
    }

    private static bool Contains(string? haystack, string needle) =>
        !string.IsNullOrEmpty(haystack)
            && haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    private bool CanGoNext()
    {
        if (IsRunning)
        {
            return false;
        }
        return CurrentStep switch
        {
            MergeWizardStep.PickTarget => SelectedTarget is not null,
            _ => false,
        };
    }

    private bool CanGoBack()
    {
        if (IsRunning)
        {
            return false;
        }
        return CurrentStep == MergeWizardStep.PreviewAndConfirm;
    }

    private bool CanConfirm() =>
        !IsRunning
            && CurrentStep == MergeWizardStep.PreviewAndConfirm
            && SelectedTarget is not null;

    private bool CanOpenMergedFile() =>
        CurrentStep == MergeWizardStep.Done
            && IsSuccess
            && !string.IsNullOrEmpty(_mergedFilePath)
            && _fileLauncher is not null;

    private async Task NextAsync()
    {
        if (CurrentStep != MergeWizardStep.PickTarget || SelectedTarget is null)
        {
            return;
        }

        var sourceId = SourceCard.Id;
        SetRunning(true, "Exporting source session via copilot --share…");
        ErrorMessage = null;

        ShareResult share;
        try
        {
            share = await _share.ExportAsync(sourceId).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Merge wizard share cancelled for {SourceId}.", sourceId);
            SetRunning(false, string.Empty);
            return;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unexpected error exporting {SourceId} for merge wizard.", sourceId);
            FailToDone($"Could not export source session: {ex.Message}");
            return;
        }

        if (!share.Success || string.IsNullOrEmpty(share.Markdown))
        {
            var msg = share.ErrorMessage ?? "copilot --share produced no output.";
            FailToDone($"Could not export source session: {msg}");
            return;
        }

        // Success path: stash the markdown for preview and advance.
        _dispatcher.Post(() =>
        {
            MarkdownPreview = share.Markdown!;
            SetRunning(false, string.Empty);
            CurrentStep = MergeWizardStep.PreviewAndConfirm;
        });
    }

    private void GoBack()
    {
        if (IsRunning)
        {
            return;
        }
        if (CurrentStep == MergeWizardStep.PreviewAndConfirm)
        {
            // Discard the cached preview so it re-fetches if the user picks
            // a different target.
            MarkdownPreview = string.Empty;
            CurrentStep = MergeWizardStep.PickTarget;
        }
    }

    private void Cancel()
    {
        if (IsRunning)
        {
            return;
        }
        _logger.LogInformation("Merge wizard cancelled by user (step={Step}).", CurrentStep);
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private async Task ConfirmAsync()
    {
        if (CurrentStep != MergeWizardStep.PreviewAndConfirm || SelectedTarget is null)
        {
            return;
        }

        var sourceId = SourceCard.Id;
        var target = SelectedTarget;
        var targetId = target.Id;

        _dispatcher.Post(() =>
        {
            SetRunning(true, $"Merging into {target.Title} ({target.ShortId})…");
            CurrentStep = MergeWizardStep.Running;
            ErrorMessage = null;
        });

        MergeResult result;
        try
        {
            result = await _merger.MergeAsync(sourceId, targetId).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Merge wizard cancelled mid-merge for {SourceId} → {TargetId}.", sourceId, targetId);
            _dispatcher.Post(() =>
            {
                SetRunning(false, string.Empty);
                CurrentStep = MergeWizardStep.PreviewAndConfirm;
            });
            return;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unexpected error during merge {SourceId} → {TargetId}.", sourceId, targetId);
            FailToDone($"Merge failed: {ex.Message}");
            return;
        }

        if (!result.Success)
        {
            FailToDone(result.ErrorMessage ?? "Merge failed.");
            return;
        }

        _resultingMergeNote = result.MergeNote;
        _mergedFilePath = TryGuessImportPath(targetId, sourceId);

        _dispatcher.Post(() =>
        {
            SetRunning(false, string.Empty);
            IsSuccess = true;
            CurrentStep = MergeWizardStep.Done;
            OnPropertyChanged(nameof(MergedFilePath));
            OnPropertyChanged(nameof(ResultingMergeNote));
            OpenMergedFileCommand.NotifyCanExecuteChanged();
        });

        try
        {
            _onMergeComplete?.Invoke(target.Card);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Post-merge dashboard refresh callback threw for {TargetId}.", targetId);
        }
    }

    /// <summary>
    /// Best-effort guess at where the engine wrote the import. The writer
    /// owns the actual path; we reconstruct it to enable the "Open merged
    /// file" affordance without changing the engine contract.
    /// </summary>
    private static string? TryGuessImportPath(string targetId, string sourceId)
    {
        try
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrEmpty(home))
            {
                return null;
            }
            var dir = Path.Combine(home, ".copilot", "session-state", targetId, "merge-imports");
            if (!Directory.Exists(dir))
            {
                return null;
            }
            // Pick the newest .md whose name mentions the source id. The
            // writer's naming scheme isn't part of the engine's public
            // contract, so we fall back to the newest file of any name.
            var matches = Directory.EnumerateFiles(dir, "*.md")
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .ToList();
            var best = matches.FirstOrDefault(f =>
                f.Name.Contains(sourceId, StringComparison.OrdinalIgnoreCase))
                ?? matches.FirstOrDefault();
            return best?.FullName;
        }
        catch
        {
            return null;
        }
    }

    private async Task OpenMergedFileAsync()
    {
        if (_fileLauncher is null || string.IsNullOrEmpty(_mergedFilePath))
        {
            return;
        }
        try
        {
            await _fileLauncher.OpenAsync(_mergedFilePath, CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not open merged file at {Path}.", _mergedFilePath);
        }
    }

    private void FailToDone(string message)
    {
        _dispatcher.Post(() =>
        {
            IsSuccess = false;
            ErrorMessage = message;
            SetRunning(false, string.Empty);
            CurrentStep = MergeWizardStep.Done;
        });
    }

    private void SetRunning(bool running, string message)
    {
        IsRunning = running;
        ProgressMessage = message;
    }

    private string SourceTargetSummary() =>
        SelectedTarget is null
            ? "the target session"
            : $"{SelectedTarget.Title} ({SelectedTarget.ShortId})";
}
