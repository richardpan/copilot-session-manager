using System.Windows.Controls;

namespace CopilotSessionManager.Views.Terminal;

/// <summary>
/// Phase 6A scaffolding (issue #159): hosts the embedded terminal tab
/// strip. Binds to <see cref="ViewModels.Terminal.TerminalTabsViewModel"/>;
/// no code-behind beyond <c>InitializeComponent</c>. UX polish (close
/// glyph, keyboard cycling, middle-click close) lands in Phase 6C; the
/// MainWindow docking + GridSplitter wiring lands in Phase 6B.
/// </summary>
public partial class TerminalTabsView : UserControl
{
    public TerminalTabsView()
    {
        InitializeComponent();
    }
}
