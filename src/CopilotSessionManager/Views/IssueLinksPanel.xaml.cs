using System.Windows.Controls;

namespace CopilotSessionManager.Views;

/// <summary>
/// Renders the row of linked-issue badges and the "+ Issue" pill on each
/// session card. All behaviour is on the bound <c>IssueLinksViewModel</c>;
/// this class is the standard XAML code-behind shell.
/// </summary>
public partial class IssueLinksPanel : UserControl
{
    public IssueLinksPanel()
    {
        InitializeComponent();
    }
}
