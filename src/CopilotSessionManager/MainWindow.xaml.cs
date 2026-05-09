using System.Windows;
using CopilotSessionManager.ViewModels;

namespace CopilotSessionManager;

/// <summary>
/// The application's main window. Wires up its <see cref="MainWindowViewModel"/>
/// via constructor injection.
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;

    public MainWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        Loaded += OnLoadedAsync;
    }

    private async void OnLoadedAsync(object sender, RoutedEventArgs e)
    {
        // Kick off the initial scan once XAML has measured + arranged so the
        // very first SessionsChanged tick has a real UI to update.
        await _viewModel.Sessions.InitializeAsync();
    }
}
