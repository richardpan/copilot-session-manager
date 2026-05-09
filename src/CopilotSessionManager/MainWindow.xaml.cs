using System.Windows;
using CopilotSessionManager.ViewModels;

namespace CopilotSessionManager;

/// <summary>
/// The application's main window. Wires up its <see cref="MainWindowViewModel"/>
/// via constructor injection.
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
