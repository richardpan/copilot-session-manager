using System;
using System.Windows;
using System.Windows.Controls;
using CopilotSessionManager.Core.Models;
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

    private async void OnLabelMenuItemClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem item)
        {
            return;
        }
        if (item.DataContext is not SessionCardViewModel card)
        {
            return;
        }
        if (item.Tag is not SessionType type)
        {
            return;
        }

        await _viewModel.Sessions.SetLabelAsync(card, type);
    }
}
