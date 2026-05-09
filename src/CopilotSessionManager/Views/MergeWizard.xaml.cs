using System;
using System.Windows;
using CopilotSessionManager.ViewModels.Merge;

namespace CopilotSessionManager.Views;

/// <summary>
/// Modal wizard window driving the <see cref="MergeWizardViewModel"/>
/// state machine. Subscribes to <see cref="MergeWizardViewModel.CloseRequested"/>
/// so the view model can dismiss the window without owning a reference to
/// it.
/// </summary>
public partial class MergeWizard : Window
{
    private MergeWizardViewModel? _viewModel;

    public MergeWizard()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Closed += OnClosed;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.CloseRequested -= OnViewModelCloseRequested;
        }
        _viewModel = e.NewValue as MergeWizardViewModel;
        if (_viewModel is not null)
        {
            _viewModel.CloseRequested += OnViewModelCloseRequested;
        }
    }

    private void OnViewModelCloseRequested(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(Close));
    }

    private void OnCancelClicked(object sender, RoutedEventArgs e)
    {
        // The footer button doubles as Cancel and Close depending on the
        // wizard state. In either case, just close the window — a cancel
        // mid-merge is not supported (ConfirmMergeCommand awaits the engine).
        Close();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.CloseRequested -= OnViewModelCloseRequested;
            _viewModel = null;
        }
    }
}
