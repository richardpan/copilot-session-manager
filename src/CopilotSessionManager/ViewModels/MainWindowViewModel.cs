using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CopilotSessionManager.Core.Cli;
using CopilotSessionManager.Core.Configuration;
using CopilotSessionManager.Core.GitHub;
using CopilotSessionManager.Core.Logging;
using CopilotSessionManager.Core.Settings;
using CopilotSessionManager.Logging;
using CopilotSessionManager.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace CopilotSessionManager.ViewModels;

/// <summary>
/// View model for <see cref="MainWindow"/>. Owns top-level UI state for the shell.
/// </summary>
public sealed partial class MainWindowViewModel : ObservableObject
{
    private static readonly Version UnknownCliVersion = new(0, 0, 0);

    private readonly ILogger<MainWindowViewModel> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly IAppSettingsStore _settingsStore;
    private readonly ILogBundler _logBundler;
    private readonly LogLevelSwitchAccessor _levelSwitch;
    private readonly IFileLauncher _fileLauncher;
    private readonly IGitHubAvailabilityProvider? _availability;
    private readonly IUiDispatcher? _dispatcher;
    private readonly ICliAvailabilityProvider _cliAvailability;
    private readonly ICliVersionProbe? _cliVersionProbe;
    private int _cliProbeStarted;

    [ObservableProperty]
    private string _title = $"{AppMetadata.ProductName} {AppMetadata.Version}";

    [ObservableProperty]
    private string _headerText = AppMetadata.ProductName;

    [ObservableProperty]
    private string _statusBarText = $"v{AppMetadata.Version} — ready";

    [ObservableProperty]
    private bool _isVerboseLogging;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowGitHubBanner))]
    private bool _isGitHubOffline;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowGitHubBanner))]
    private bool _isGitHubUnauthenticated;

    [ObservableProperty]
    private string _gitHubStatusMessage = string.Empty;

    /// <summary>
    /// True when either the offline or unauthenticated GitHub banner should
    /// be visible. Exposed as a single derived flag so the XAML banner
    /// container can collapse cleanly without a multi-converter, and so the
    /// state-machine is trivially testable from a unit test.
    /// </summary>
    public bool ShowGitHubBanner => IsGitHubOffline || IsGitHubUnauthenticated;

    public MainWindowViewModel(
        SessionsViewModel sessions,
        IServiceProvider serviceProvider,
        IAppSettingsStore settingsStore,
        ILogBundler logBundler,
        LogLevelSwitchAccessor levelSwitch,
        IFileLauncher fileLauncher,
        ILogger<MainWindowViewModel> logger)
        : this(
            sessions,
            serviceProvider,
            settingsStore,
            logBundler,
            levelSwitch,
            fileLauncher,
            availability: null,
            dispatcher: null,
            cliAvailability: null,
            cliVersionProbe: null,
            logger)
    {
    }

    public MainWindowViewModel(
        SessionsViewModel sessions,
        IServiceProvider serviceProvider,
        IAppSettingsStore settingsStore,
        ILogBundler logBundler,
        LogLevelSwitchAccessor levelSwitch,
        IFileLauncher fileLauncher,
        IGitHubAvailabilityProvider? availability,
        IUiDispatcher? dispatcher,
        ILogger<MainWindowViewModel> logger)
        : this(
            sessions,
            serviceProvider,
            settingsStore,
            logBundler,
            levelSwitch,
            fileLauncher,
            availability,
            dispatcher,
            cliAvailability: null,
            cliVersionProbe: null,
            logger)
    {
    }

    /// <summary>
    /// DI-preferred constructor. Subscribes to
    /// <see cref="IGitHubAvailabilityProvider.AvailabilityChanged"/> so the
    /// shell can show an offline / unauth banner. The dispatcher is used to
    /// marshal property updates back to the UI thread (the provider may
    /// raise events from any worker thread).
    /// </summary>
    public MainWindowViewModel(
        SessionsViewModel sessions,
        IServiceProvider serviceProvider,
        IAppSettingsStore settingsStore,
        ILogBundler logBundler,
        LogLevelSwitchAccessor levelSwitch,
        IFileLauncher fileLauncher,
        IGitHubAvailabilityProvider? availability,
        IUiDispatcher? dispatcher,
        ICliAvailabilityProvider? cliAvailability,
        ICliVersionProbe? cliVersionProbe,
        ILogger<MainWindowViewModel> logger)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(serviceProvider);
        ArgumentNullException.ThrowIfNull(settingsStore);
        ArgumentNullException.ThrowIfNull(logBundler);
        ArgumentNullException.ThrowIfNull(levelSwitch);
        ArgumentNullException.ThrowIfNull(fileLauncher);
        ArgumentNullException.ThrowIfNull(logger);

        Sessions = sessions;
        _serviceProvider = serviceProvider;
        _settingsStore = settingsStore;
        _logBundler = logBundler;
        _levelSwitch = levelSwitch;
        _fileLauncher = fileLauncher;
        _availability = availability;
        _dispatcher = dispatcher;
        _cliAvailability = cliAvailability ?? new CliAvailabilityProvider();
        _cliVersionProbe = cliVersionProbe;
        _logger = logger;
        OutdatedCliBanner = new OutdatedCliBannerViewModel(_cliAvailability);
        // V1.4 (#159): optional embedded tabs surface resolved from DI.
        // Tests build MainWindowViewModel without a provider that has
        // TerminalTabsViewModel registered, so the optional resolve
        // keeps them green.
        TerminalTabs = serviceProvider.GetService(typeof(ViewModels.Terminal.TerminalTabsViewModel))
            as ViewModels.Terminal.TerminalTabsViewModel;

        _isVerboseLogging = _levelSwitch.IsVerbose;

        if (_availability is not null)
        {
            ApplyAvailability(_availability.Current);
            _availability.AvailabilityChanged += OnAvailabilityChanged;
        }

        _logger.LogInformation("MainWindowViewModel constructed.");
    }

    public SessionsViewModel Sessions { get; }

    public OutdatedCliBannerViewModel OutdatedCliBanner { get; }

    /// <summary>
    /// V1.4 (#159) embedded tabbed-terminal surface bound by
    /// <c>MainWindow.xaml</c>'s docked <c>TerminalTabsView</c>. Null when
    /// the host's <see cref="IServiceProvider"/> does not register the
    /// tabs view-model (unit-test fixtures), in which case the XAML
    /// hides the pane.
    /// </summary>
    public ViewModels.Terminal.TerminalTabsViewModel? TerminalTabs { get; }

    /// <summary>Opens the first-run onboarding window modally so the user can
    /// re-run the welcome flow at any time. Bound to the Help → Onboarding…
    /// menu entry.</summary>
    [RelayCommand]
    public void OpenOnboarding()
    {
        try
        {
            var window = _serviceProvider.GetRequiredService<OnboardingWindow>();
            window.Owner = System.Windows.Application.Current?.MainWindow;
            window.WindowStartupLocation = window.Owner is null
                ? System.Windows.WindowStartupLocation.CenterScreen
                : System.Windows.WindowStartupLocation.CenterOwner;
            window.ShowDialog();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open onboarding window from Help menu.");
        }
    }

    /// <summary>Opens the per-user log folder in Explorer.</summary>
    [RelayCommand]
    public async Task OpenLogFolderAsync()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.LogsDirectory);
            await _fileLauncher.OpenAsync(AppPaths.LogsDirectory).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open log folder.");
        }
    }

    /// <summary>Prompts for a destination zip and bundles the current logs.</summary>
    [RelayCommand]
    public async Task BundleLogsAsync()
    {
        try
        {
            var dialog = new SaveFileDialog
            {
                Title = "Save Copilot Session Manager log bundle",
                Filter = "Zip archive (*.zip)|*.zip",
                FileName = $"copilot-session-manager-logs-{DateTime.UtcNow:yyyyMMdd-HHmmss}.zip",
                AddExtension = true,
                DefaultExt = ".zip",
                OverwritePrompt = true,
            };

            var owner = System.Windows.Application.Current?.MainWindow;
            var picked = owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
            if (picked != true)
            {
                return;
            }

            var result = await _logBundler.BundleAsync(dialog.FileName).ConfigureAwait(false);
            StatusBarText = $"Bundled {result.FileCount} log file(s) → {Path.GetFileName(result.DestinationPath)}";
            _logger.LogInformation(
                "Wrote log bundle to {DestinationPath} ({FileCount} files, {TotalBytes} bytes).",
                result.DestinationPath, result.FileCount, result.TotalBytes);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to bundle logs.");
            StatusBarText = "Failed to bundle logs — see app log.";
        }
    }

    /// <summary>Toggles between Information and Debug verbosity, live + persisted.</summary>
    [RelayCommand]
    public async Task ToggleVerboseLoggingAsync()
    {
        try
        {
            _levelSwitch.SetVerbose(IsVerboseLogging);
            var settings = await _settingsStore.LoadAsync().ConfigureAwait(false);
            settings.LogLevel = IsVerboseLogging ? "Debug" : "Information";
            await _settingsStore.SaveAsync(settings).ConfigureAwait(false);
            _logger.LogInformation("Log level switched to {LogLevel}.", settings.LogLevel);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist verbose logging setting.");
        }
    }

    /// <summary>
    /// Loads user settings and runs all per-launch startup tasks for the
    /// shell — currently the initial session scan and (when opted in via
    /// <see cref="AppSettings.AutoCleanStaleLocksOnStartup"/>) a one-shot
    /// stale-lock sweep. Called from <c>MainWindow.OnLoadedAsync</c> once
    /// XAML has measured + arranged.
    /// </summary>
    public async Task RunStartupTasksAsync(CancellationToken cancellationToken = default)
    {
        StartCliVersionProbeOnce(cancellationToken);

        var autoClean = false;
        try
        {
            var settings = await _settingsStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            autoClean = settings.AutoCleanStaleLocksOnStartup;
        }
        catch (Exception ex)
        {
            // Failing to load settings must not block the dashboard from
            // loading. Default to the historical no-op behaviour.
            _logger.LogWarning(ex, "Could not load settings before startup tasks; defaulting to no auto-clean.");
        }

        await Sessions.InitializeAsync(autoClean, cancellationToken).ConfigureAwait(false);
    }

    private void StartCliVersionProbeOnce(CancellationToken cancellationToken)
    {
        if (_cliVersionProbe is null || Interlocked.Exchange(ref _cliProbeStarted, 1) == 1)
        {
            return;
        }

        _ = ProbeCliVersionsAsync(cancellationToken);
    }

    private async Task ProbeCliVersionsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var probes = await _cliVersionProbe!.ProbeAsync(cancellationToken);
            var state = ClassifyCliAvailability(probes);
            _cliAvailability.Report(state, probes, BuildCliAvailabilityMessage(state, probes));
            _logger.LogInformation("CLI version probe completed with state {State}.", state);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug("CLI version probe cancelled during startup.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CLI version probe failed during startup.");
        }
    }

    private static CliAvailability ClassifyCliAvailability(IReadOnlyList<CliVersionInfo> probes)
    {
        if (probes.Any(probe => probe.IsOutdated && probe.Detected.Equals(UnknownCliVersion)))
        {
            return CliAvailability.NotInstalled;
        }

        return probes.Any(static probe => probe.IsOutdated)
            ? CliAvailability.Outdated
            : CliAvailability.Available;
    }

    private static string? BuildCliAvailabilityMessage(CliAvailability state, IReadOnlyList<CliVersionInfo> probes) =>
        state switch
        {
            CliAvailability.Available => null,
            CliAvailability.NotInstalled => "One or more required CLI tools are not installed or could not be probed.",
            _ => string.Join(", ", probes
                .Where(static probe => probe.IsOutdated)
                .Select(static probe => $"{probe.Cli} {probe.Detected} < {probe.Minimum}")),
        };

    private void OnAvailabilityChanged(object? sender, GitHubAvailabilityState state)
    {
        if (_dispatcher is null)
        {
            ApplyAvailability(state);
        }
        else
        {
            _dispatcher.Post(() => ApplyAvailability(state));
        }
    }

    private void ApplyAvailability(GitHubAvailabilityState state)
    {
        IsGitHubOffline = state.State == GitHubAvailability.Offline;
        IsGitHubUnauthenticated = state.State == GitHubAvailability.Unauthenticated;
        GitHubStatusMessage = state.UserMessage ?? string.Empty;
    }
}
