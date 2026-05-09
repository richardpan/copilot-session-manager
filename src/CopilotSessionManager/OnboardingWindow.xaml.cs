using System.ComponentModel;
using System.Windows;
using CopilotSessionManager.ViewModels;

namespace CopilotSessionManager;

/// <summary>
/// First-run / Help-menu onboarding window. Three pages: welcome,
/// prerequisite checks, and adoption preview. Closes when the bound
/// view model raises <see cref="OnboardingViewModel.IsComplete"/>.
/// </summary>
public partial class OnboardingWindow : Window
{
    private OnboardingViewModel? _viewModel;

    public OnboardingWindow(OnboardingViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        Closed += OnClosed;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(OnboardingViewModel.IsComplete)
            && _viewModel?.IsComplete == true)
        {
            Close();
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel = null;
        }
        Closed -= OnClosed;
    }
}
