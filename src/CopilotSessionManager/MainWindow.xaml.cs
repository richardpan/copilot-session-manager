using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using CopilotSessionManager.Core.Cli;
using CopilotSessionManager.Core.Models;
using CopilotSessionManager.Core.Sessions;
using CopilotSessionManager.ViewModels;
using CopilotSessionManager.Views;
using Microsoft.Extensions.Logging;

namespace CopilotSessionManager;

/// <summary>
/// The application's main window. Wires up its <see cref="MainWindowViewModel"/>
/// via constructor injection.
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;
    private readonly ISubagentScanService _subagentScanService;
    private readonly ICopilotCliAdapter _cliAdapter;
    private readonly ICopilotPaths _copilotPaths;
    private readonly ILogger<MainWindow> _logger;

    public MainWindow(
        MainWindowViewModel viewModel,
        ISubagentScanService subagentScanService,
        ICopilotCliAdapter cliAdapter,
        ICopilotPaths copilotPaths,
        ILogger<MainWindow> logger)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _subagentScanService = subagentScanService;
        _cliAdapter = cliAdapter;
        _copilotPaths = copilotPaths;
        _logger = logger;
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

    private double _gridScrollAccumulator;

    /// <summary>
    /// Reduces scroll sensitivity on the sessions DataGrid to 1 row per
    /// mouse-wheel notch. Accumulates wheel deltas (important for precision
    /// trackpads that send many small increments) and only scrolls once
    /// enough delta has built up — requiring roughly double the default
    /// input to move one row.
    /// </summary>
    private void SessionsGrid_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not DataGrid grid)
        {
            return;
        }

        var scrollViewer = FindVisualChild<ScrollViewer>(grid);
        if (scrollViewer is null)
        {
            return;
        }

        _gridScrollAccumulator += e.Delta;

        // Standard notch = ±120. Require 180 accumulated delta per row
        // — a middle ground between default (120) and half-speed (240).
        const double deltaPerRow = 180.0;
        var rows = (int)(_gridScrollAccumulator / deltaPerRow);
        if (rows == 0)
        {
            e.Handled = true;
            return;
        }

        _gridScrollAccumulator -= rows * deltaPerRow;

        var absRows = Math.Abs(rows);
        for (int i = 0; i < absRows; i++)
        {
            if (rows > 0)
            {
                scrollViewer.LineUp();
            }
            else
            {
                scrollViewer.LineDown();
            }
        }

        e.Handled = true;
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T found)
            {
                return found;
            }

            var result = FindVisualChild<T>(child);
            if (result is not null)
            {
                return result;
            }
        }
        return null;
    }

    /// <summary>
    /// Toggles the row-details (sub-agent breakdown) panel: when a user clicks
    /// a row whose details are already showing, collapse them by clearing the
    /// selection. With <c>RowDetailsVisibilityMode="VisibleWhenSelected"</c>
    /// the details panel is bound to selection, so deselecting the row
    /// hides the panel — giving us click-to-expand / click-to-collapse.
    /// </summary>
    private void DataGrid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DataGrid grid || e.OriginalSource is not DependencyObject source)
        {
            return;
        }

        // Walk up the visual tree from the click target. If we hit an
        // interactive control (button, textbox) or the details presenter
        // before we reach a DataGridRow, bail — the click is meant for that
        // inner control, not for toggling the row.
        DependencyObject? cursor = source;
        while (cursor is not null)
        {
            switch (cursor)
            {
                case ButtonBase:
                case TextBoxBase:
                case DataGridDetailsPresenter:
                    return;
                case DataGridRow row:
                    if (ReferenceEquals(row.Item, grid.SelectedItem))
                    {
                        grid.SelectedItem = null;
                        row.IsSelected = false;
                        e.Handled = true;
                    }
                    return;
            }
            cursor = VisualTreeHelper.GetParent(cursor);
        }
    }

    // TODO(#131-followup): pre-scan recent sessions for badge counts so the badge appears before row expansion.
    private async void DataGrid_LoadingRowDetails(object sender, DataGridRowDetailsEventArgs e)
    {
        if (e.Row.Item is not SessionCardViewModel card)
        {
            return;
        }

        try
        {
            await card.LoadSubagentsAsync(_subagentScanService);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load sub-agents for session {SessionId}.", card.Id);
            _viewModel.Sessions.StatusMessage = $"Could not load sub-agents for {card.ShortId}: {ex.Message}";
        }
    }

    private void OnLabelChipClicked(object sender, MouseButtonEventArgs e)
    {
        // Single left-click on the label chip should open the same submenu the
        // ContextMenu provided previously. Without this handler users had
        // to right-click — which is non-obvious for a touch-style chip. The
        // ContextMenu is still wired up via Border.ContextMenu so right-click
        // and keyboard menu key continue to work, but a normal click now opens
        // the dropdown anchored to the chip itself.
        //
        // We MUST set DataContext on the ContextMenu explicitly — manually
        // opening a ContextMenu via IsOpen=true skips the WPF code path that
        // would otherwise inherit DataContext from the PlacementTarget, so
        // MenuItems further down would see a null DataContext and the click
        // handler couldn't resolve the source SessionCardViewModel.
        if (sender is not FrameworkElement fe || fe.ContextMenu is null)
        {
            return;
        }
        fe.ContextMenu.PlacementTarget = fe;
        fe.ContextMenu.DataContext = fe.DataContext;
        fe.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        fe.ContextMenu.IsOpen = true;
        e.Handled = true;
    }

    private async void OnLabelMenuItemClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem item)
        {
            return;
        }
        if (item.Tag is not SessionType type)
        {
            return;
        }

        // Resolve the source SessionCardViewModel via a chain of fallbacks:
        // (1) the MenuItem's inherited DataContext (works when ContextMenu was
        //     opened via right-click OR via OnLabelChipClicked, which now
        //     propagates DataContext explicitly);
        // (2) walk up to the ContextMenu and read PlacementTarget.DataContext
        //     for any edge case where inheritance wasn't yet propagated.
        var card = (item.DataContext as SessionCardViewModel) ?? FindSessionCardContext(item);
        if (card is null)
        {
            return;
        }

        await _viewModel.Sessions.SetLabelAsync(card, type);
    }

    private void OnViewTokensClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element ||
            element.DataContext is not SessionCardViewModel card)
        {
            return;
        }

        try
        {
            var window = new SessionTokensWindow(card, _cliAdapter, _copilotPaths)
            {
                Owner = this,
            };
            window.ShowDialog();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open token usage window for {SessionId}.", card.Id);
            _viewModel.Sessions.StatusMessage = $"Could not show tokens for {card.ShortId}: {ex.Message}";
        }
    }

    private static SessionCardViewModel? FindSessionCardContext(MenuItem item)
    {
        // Walk up the logical tree (MenuItem → parent MenuItem → ContextMenu).
        DependencyObject? cursor = item;
        while (cursor is not null)
        {
            if (cursor is FrameworkElement fe && fe.DataContext is SessionCardViewModel card)
            {
                return card;
            }
            // ContextMenu's PlacementTarget points at the row we right-clicked.
            if (cursor is ContextMenu menu)
            {
                if (menu.PlacementTarget is FrameworkElement target &&
                    target.DataContext is SessionCardViewModel targetCard)
                {
                    return targetCard;
                }
                break;
            }
            cursor = LogicalTreeHelper.GetParent(cursor) ?? VisualTreeHelper.GetParent(cursor);
        }
        return null;
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

    /// <summary>
    /// Debug menu entry (V1.4, #170 Phase 3E): open a modeless
    /// <see cref="TerminalWindow"/> that hosts an embedded
    /// <c>TerminalControl</c> wired to a fresh <c>pwsh -NoLogo</c> session
    /// over ConPTY. Used for live validation of the end-to-end
    /// terminal pipeline before Phase 4 turns this into the default
    /// "Open terminal" affordance.
    /// </summary>
    private void OnOpenEmbeddedTerminalClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            var window = new TerminalWindow
            {
                Owner = this,
            };
            window.Show();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open embedded terminal debug window.");
            MessageBox.Show(
                this,
                $"Could not open the embedded terminal:{Environment.NewLine}{Environment.NewLine}{ex.Message}",
                "Embedded terminal",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }
}

