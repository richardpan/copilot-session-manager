using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CopilotSessionManager.Core.Models;
using CopilotSessionManager.Core.Onboarding;
using CopilotSessionManager.Core.Sessions;
using CopilotSessionManager.Core.Settings;
using CopilotSessionManager.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CopilotSessionManager.ViewModels;

/// <summary>
/// Logical step in the onboarding flow. Drives which page is rendered.
/// </summary>
public enum OnboardingStep
{
    Welcome = 0,
    Prerequisites = 1,
    Adoption = 2,
    Done = 3,
}

/// <summary>
/// View model behind <c>OnboardingWindow</c>. Three-step flow:
/// <list type="number">
///   <item>Welcome — short pitch + screenshot.</item>
///   <item>Prerequisites — runs <see cref="IPrerequisiteChecker"/> and shows
///   the results with install links.</item>
///   <item>Adoption — counts existing sessions and previews the first few.</item>
/// </list>
/// Skip is allowed on every step and persists <c>OnboardingCompleted=true</c>.
/// </summary>
public sealed partial class OnboardingViewModel : ObservableObject
{
    private readonly IPrerequisiteChecker _checker;
    private readonly IAppSettingsStore _settingsStore;
    private readonly ISessionDiscoveryService _discovery;
    private readonly IFileLauncher _fileLauncher;
    private readonly ILogger _logger;

    [ObservableProperty]
    private OnboardingStep _currentStep = OnboardingStep.Welcome;

    [ObservableProperty]
    private bool _isCheckingPrerequisites;

    [ObservableProperty]
    private bool _isLoadingAdoption;

    [ObservableProperty]
    private int _existingSessionCount;

    /// <summary>True once the user has finished or skipped the flow. Bound by
    /// the host window to close itself.</summary>
    [ObservableProperty]
    private bool _isComplete;

    public ObservableCollection<PrerequisiteResultViewModel> PrerequisiteResults { get; } = new();
    public ObservableCollection<AdoptedSessionPreview> AdoptionPreview { get; } = new();

    /// <summary>1-based step number for the "STEP n OF 3" stepper text.</summary>
    public int StepNumber => (int)CurrentStep + 1;

    /// <summary>Width in pixels of the filled portion of the progress bar
    /// (the bar itself is 180px wide).</summary>
    public double ProgressWidth => CurrentStep switch
    {
        OnboardingStep.Welcome => 60,
        OnboardingStep.Prerequisites => 120,
        OnboardingStep.Adoption => 180,
        OnboardingStep.Done => 180,
        _ => 0,
    };

    /// <summary>True when the prereq check is NOT running. Bound by the
    /// "Re-check" button's IsEnabled.</summary>
    public bool CanRecheck => !IsCheckingPrerequisites;

    partial void OnCurrentStepChanged(OnboardingStep value)
    {
        OnPropertyChanged(nameof(StepNumber));
        OnPropertyChanged(nameof(ProgressWidth));
    }

    partial void OnIsCheckingPrerequisitesChanged(bool value)
    {
        OnPropertyChanged(nameof(CanRecheck));
    }

    public OnboardingViewModel(
        IPrerequisiteChecker checker,
        IAppSettingsStore settingsStore,
        ISessionDiscoveryService discovery,
        IFileLauncher fileLauncher,
        ILogger<OnboardingViewModel>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(checker);
        ArgumentNullException.ThrowIfNull(settingsStore);
        ArgumentNullException.ThrowIfNull(discovery);
        ArgumentNullException.ThrowIfNull(fileLauncher);

        _checker = checker;
        _settingsStore = settingsStore;
        _discovery = discovery;
        _fileLauncher = fileLauncher;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    /// <summary>True if any prereq is currently classified as Failed. Drives
    /// whether the "Continue" button on the prereq page is highlighted as
    /// "Continue anyway" vs "Continue".</summary>
    public bool HasFailedPrerequisites
    {
        get
        {
            foreach (var p in PrerequisiteResults)
            {
                if (p.Status == PrerequisiteStatus.Failed)
                    return true;
            }
            return false;
        }
    }

    [RelayCommand]
    public async Task NextAsync()
    {
        switch (CurrentStep)
        {
            case OnboardingStep.Welcome:
                CurrentStep = OnboardingStep.Prerequisites;
                await RecheckPrerequisitesAsync().ConfigureAwait(true);
                break;

            case OnboardingStep.Prerequisites:
                CurrentStep = OnboardingStep.Adoption;
                await LoadAdoptionPreviewAsync().ConfigureAwait(true);
                break;

            case OnboardingStep.Adoption:
                await CompleteAsync().ConfigureAwait(true);
                break;
        }
    }

    [RelayCommand]
    public void Back()
    {
        CurrentStep = CurrentStep switch
        {
            OnboardingStep.Prerequisites => OnboardingStep.Welcome,
            OnboardingStep.Adoption => OnboardingStep.Prerequisites,
            _ => CurrentStep,
        };
    }

    [RelayCommand]
    public Task SkipAsync() => CompleteAsync();

    [RelayCommand]
    public async Task RecheckPrerequisitesAsync()
    {
        IsCheckingPrerequisites = true;
        try
        {
            var results = await _checker.CheckAllAsync().ConfigureAwait(true);
            PrerequisiteResults.Clear();
            foreach (var r in results)
            {
                PrerequisiteResults.Add(new PrerequisiteResultViewModel(r));
            }
            OnPropertyChanged(nameof(HasFailedPrerequisites));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Prerequisite check failed.");
        }
        finally
        {
            IsCheckingPrerequisites = false;
        }
    }

    [RelayCommand]
    public async Task OpenInstallUrlAsync(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return;
        try
        {
            await _fileLauncher.OpenAsync(url).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to open URL {Url}.", url);
        }
    }

    /// <summary>Persists OnboardingCompleted and signals the window to close.</summary>
    private async Task CompleteAsync()
    {
        try
        {
            var settings = await _settingsStore.LoadAsync().ConfigureAwait(true);
            settings.OnboardingCompleted = true;
            await _settingsStore.SaveAsync(settings).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist OnboardingCompleted; closing window anyway.");
        }
        finally
        {
            CurrentStep = OnboardingStep.Done;
            IsComplete = true;
        }
    }

    private async Task LoadAdoptionPreviewAsync()
    {
        IsLoadingAdoption = true;
        try
        {
            AdoptionPreview.Clear();
            var sessions = await _discovery.ScanAsync().ConfigureAwait(true);
            ExistingSessionCount = sessions.Count;
            var max = Math.Min(5, sessions.Count);
            for (var i = 0; i < max; i++)
            {
                AdoptionPreview.Add(new AdoptedSessionPreview(sessions[i]));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load adoption preview.");
            ExistingSessionCount = 0;
        }
        finally
        {
            IsLoadingAdoption = false;
        }
    }
}

/// <summary>UI projection of a single <see cref="PrerequisiteResult"/>.</summary>
public sealed class PrerequisiteResultViewModel
{
    public PrerequisiteResultViewModel(PrerequisiteResult model)
    {
        ArgumentNullException.ThrowIfNull(model);
        Name = model.Name;
        Status = model.Status;
        Detail = model.Detail;
        InstallUrl = model.InstallUrl;
    }

    public string Name { get; }
    public PrerequisiteStatus Status { get; }
    public string Detail { get; }
    public string? InstallUrl { get; }

    public string StatusGlyph => Status switch
    {
        PrerequisiteStatus.Ok => "✓",
        PrerequisiteStatus.Warning => "⚠",
        PrerequisiteStatus.Failed => "✗",
        _ => "?",
    };

    public bool HasInstallUrl => !string.IsNullOrEmpty(InstallUrl);
}

/// <summary>Compact preview of an existing session shown on the adoption page.</summary>
public sealed class AdoptedSessionPreview
{
    public AdoptedSessionPreview(Session session)
    {
        ArgumentNullException.ThrowIfNull(session);
        ShortId = session.Id.Length >= 8 ? session.Id[..8] : session.Id;
        Title = !string.IsNullOrWhiteSpace(session.Summary) ? session.Summary!
            : !string.IsNullOrWhiteSpace(session.Repository) ? session.Repository!
            : ShortId;
        Repository = session.Repository ?? "(no repo)";
    }

    public string ShortId { get; }
    public string Title { get; }
    public string Repository { get; }
}
