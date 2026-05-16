using System.Windows.Controls;
using System.Windows.Input;
using CopilotSessionManager.ViewModels.Terminal;

namespace CopilotSessionManager.Views.Terminal;

/// <summary>
/// Phase 6A scaffolding (issue #159): hosts the embedded terminal tab
/// strip. Binds to <see cref="ViewModels.Terminal.TerminalTabsViewModel"/>.
/// Phase 6C adds middle-click-to-close support via
/// <see cref="OnTabItemMouseDown"/>; the rest of the UX polish (close
/// glyph + Ctrl+Tab cycling) is driven from the XAML.
/// </summary>
public partial class TerminalTabsView : UserControl
{
    public TerminalTabsView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Phase 6C (#159): middle-button click on any tab header closes
    /// that tab. The handler is attached via an <c>EventSetter</c> in
    /// the <see cref="TabControl.Resources"/> style so every header in
    /// the strip picks it up automatically. Routes through the
    /// view-model's <c>CloseTabCommand</c> so the close path is
    /// identical to the close glyph and any future external callers.
    /// </summary>
    private void OnTabItemMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle)
        {
            return;
        }
        if (sender is not TabItem item || item.DataContext is not TerminalTabViewModel tab)
        {
            return;
        }
        if (DataContext is not TerminalTabsViewModel vm)
        {
            return;
        }
        if (vm.CloseTabCommand.CanExecute(tab))
        {
            vm.CloseTabCommand.Execute(tab);
            e.Handled = true;
        }
    }
}
