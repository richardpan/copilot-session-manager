using System.Windows;
using CopilotSessionManager.Core.GitHub.Issues;

namespace CopilotSessionManager.Views;

/// <summary>
/// Modal dialog that collects a single GitHub issue reference. Accepts
/// <c>owner/repo#NN</c>, <c>#NN</c>, plain <c>NN</c>, or a full issue URL;
/// validates as the user types and only enables "Add" once the input is
/// parseable. Use <see cref="TryShow"/> as the entry point.
/// </summary>
public partial class AddIssueDialog : Window
{
    private readonly string? _defaultOwnerRepo;

    private AddIssueDialog(string? defaultOwnerRepo)
    {
        _defaultOwnerRepo = defaultOwnerRepo;
        InitializeComponent();

        if (!string.IsNullOrWhiteSpace(defaultOwnerRepo))
        {
            HintText.Text = $"Accepts owner/repo#NN, #NN, NN (defaults to {defaultOwnerRepo}), or full URL.";
        }
    }

    /// <summary>The parsed issue ref once the user clicks Add.</summary>
    public IssueRef? Result { get; private set; }

    /// <summary>
    /// Shows the dialog modally over <paramref name="owner"/>. On OK,
    /// returns true and writes the parsed ref to <paramref name="result"/>.
    /// On Cancel or close, returns false and sets <paramref name="result"/>
    /// to null. <paramref name="defaultOwnerRepo"/> is used to resolve
    /// short-form input like <c>#42</c>.
    /// </summary>
    public static bool TryShow(Window? owner, string? defaultOwnerRepo, out IssueRef? result)
    {
        var dialog = new AddIssueDialog(defaultOwnerRepo);
        if (owner is not null)
        {
            dialog.Owner = owner;
        }
        var ok = dialog.ShowDialog() == true;
        result = ok ? dialog.Result : null;
        return ok && result is not null;
    }

    private void InputBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        var text = InputBox.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            ValidationText.Text = string.Empty;
            AddButton.IsEnabled = false;
            return;
        }

        if (IssueRefParser.TryParse(text, _defaultOwnerRepo, out var parsed))
        {
            ValidationText.Text = $"Will link {parsed}";
            ValidationText.Foreground = System.Windows.Media.Brushes.LightGreen;
            AddButton.IsEnabled = true;
        }
        else
        {
            ValidationText.Foreground = (System.Windows.Media.Brush)FindResource("ErrorBrush") is { } b
                ? b
                : System.Windows.Media.Brushes.IndianRed;
            ValidationText.Text = string.IsNullOrWhiteSpace(_defaultOwnerRepo)
                ? "Use owner/repo#NN or a full GitHub issue URL (no session repo to default to)."
                : "Could not parse — try owner/repo#NN, #NN, or a full GitHub issue URL.";
            AddButton.IsEnabled = false;
        }
    }

    private void AddButton_Click(object sender, RoutedEventArgs e)
    {
        if (IssueRefParser.TryParse(InputBox.Text, _defaultOwnerRepo, out var parsed))
        {
            Result = parsed;
            DialogResult = true;
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        Result = null;
        DialogResult = false;
    }
}
