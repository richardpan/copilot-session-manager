using CommunityToolkit.Mvvm.ComponentModel;
using CopilotSessionManager.Core.Configuration;
using Microsoft.Extensions.Logging;

namespace CopilotSessionManager.ViewModels;

/// <summary>
/// View model for <see cref="MainWindow"/>. Owns top-level UI state for the shell.
/// </summary>
public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly ILogger<MainWindowViewModel> _logger;

    [ObservableProperty]
    private string _title = $"{AppMetadata.ProductName} {AppMetadata.Version}";

    [ObservableProperty]
    private string _headerText = AppMetadata.ProductName;

    [ObservableProperty]
    private string _welcomeText = "Welcome.";

    [ObservableProperty]
    private string _statusBarText = $"v{AppMetadata.Version} — ready";

    public MainWindowViewModel(ILogger<MainWindowViewModel> logger)
    {
        _logger = logger;
        _logger.LogInformation("MainWindowViewModel constructed.");
    }
}
