using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
        // RunStartupTasksAsync also performs the V1.8 (#74) opt-in stale-lock
        // sweep when AppSettings.AutoCleanStaleLocksOnStartup is true.
        await _viewModel.RunStartupTasksAsync();
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

    /// <summary>
    /// Click handler for the title TextBlock — switches the card into inline
    /// rename mode (#105). Triggered on a single left-click anywhere on the
    /// title text. Right-clicks fall through to the context menu instead.
    /// </summary>
    private void OnCardTitleClicked(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe)
        {
            return;
        }
        if (fe.DataContext is not SessionCardViewModel card)
        {
            return;
        }
        if (card.BeginRenameCommand.CanExecute(null))
        {
            card.BeginRenameCommand.Execute(null);
            // Stop the click from bubbling up to the card border (which would
            // otherwise steal focus back).
            e.Handled = true;
        }
    }

    /// <summary>
    /// Auto-focuses the rename TextBox the moment it becomes visible and
    /// selects all the text so the user can immediately type a replacement.
    /// </summary>
    private void OnCardTitleEditorVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not TextBox box)
        {
            return;
        }
        if (e.NewValue is not bool nowVisible || !nowVisible)
        {
            return;
        }
        // Defer to the dispatcher so the TextBox has been laid out before we
        // try to focus / select.
        box.Dispatcher.BeginInvoke(new Action(() =>
        {
            box.Focus();
            box.SelectAll();
        }), System.Windows.Threading.DispatcherPriority.Input);
    }

    /// <summary>
    /// Enter commits the rename, Esc cancels it. Anything else passes through.
    /// </summary>
    private void OnCardTitleEditorKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box || box.DataContext is not SessionCardViewModel card)
        {
            return;
        }
        switch (e.Key)
        {
            case Key.Enter:
                if (card.CommitRenameCommand.CanExecute(null))
                {
                    card.CommitRenameCommand.Execute(null);
                }
                e.Handled = true;
                break;
            case Key.Escape:
                if (card.CancelRenameCommand.CanExecute(null))
                {
                    card.CancelRenameCommand.Execute(null);
                }
                e.Handled = true;
                break;
        }
    }

    /// <summary>
    /// Treats losing keyboard focus as an implicit commit so users can click
    /// away to save without thinking about Enter vs Esc.
    /// </summary>
    private void OnCardTitleEditorLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is not TextBox box || box.DataContext is not SessionCardViewModel card)
        {
            return;
        }
        if (!card.IsEditingTitle)
        {
            return;
        }
        if (card.CommitRenameCommand.CanExecute(null))
        {
            card.CommitRenameCommand.Execute(null);
        }
    }

    /// <summary>
    /// V1.3 (#110): Esc clears the search box (returning to match-all).
    /// All other keys pass through unchanged so typing filters live.
    /// </summary>
    private void OnSearchBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box)
        {
            return;
        }
        if (e.Key == Key.Escape && box.Text.Length > 0)
        {
            box.Clear();
            e.Handled = true;
        }
    }
}

