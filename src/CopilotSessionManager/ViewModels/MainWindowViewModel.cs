using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CopilotSessionManager.Core.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CopilotSessionManager.ViewModels;

/// <summary>
/// View model for <see cref="MainWindow"/>. Owns top-level UI state for the shell.
/// </summary>
public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly ILogger<MainWindowViewModel> _logger;
    private readonly IServiceProvider _serviceProvider;

    [ObservableProperty]
    private string _title = $"{AppMetadata.ProductName} {AppMetadata.Version}";

    [ObservableProperty]
    private string _headerText = AppMetadata.ProductName;

    [ObservableProperty]
    private string _statusBarText = $"v{AppMetadata.Version} — ready";

    public MainWindowViewModel(
        SessionsViewModel sessions,
        IServiceProvider serviceProvider,
        ILogger<MainWindowViewModel> logger)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(serviceProvider);
        ArgumentNullException.ThrowIfNull(logger);

        Sessions = sessions;
        _serviceProvider = serviceProvider;
        _logger = logger;
        _logger.LogInformation("MainWindowViewModel constructed.");
    }

    public SessionsViewModel Sessions { get; }

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
}
