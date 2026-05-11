using System.IO;
using FluentAssertions;
using Xunit;

namespace CopilotSessionManager.Tests.Views;

/// <summary>
/// V1.6 (#118) source-pinning tests for the docs launcher row in
/// <c>MainWindow.xaml</c>:
/// <list type="bullet">
///   <item>Prominent <c>📚 Docs</c> button is present and bound to <c>OpenDocsCommand</c>.</item>
///   <item>The Actions column appears to the left of the Name column.</item>
///   <item>The hover-revealed opacity trigger from V1.5 is gone (always-visible actions).</item>
///   <item>The inline 📄 README button is gone from the row (replaced by the context menu entry).</item>
///   <item>The pre-existing "Open SESSION-README" context menu entry still exists.</item>
///   <item>A new "Open SESSION-DOCS" context menu entry is present.</item>
/// </list>
/// </summary>
public class MainWindowDocsButtonTests
{
    [Fact]
    public void MainWindow_HasProminentDocsButton_BoundToOpenDocsCommand()
    {
        var content = ReadMainWindowXaml();

        content.Should().Contain("Content=\"📚 Docs\"",
            "the V1.6 primary docs button must use the literal text label '📚 Docs'");
        content.Should().Contain("OpenDocsCommand",
            "the docs button must bind to SessionsViewModel.OpenDocsCommand");
        content.Should().Contain(
            "Command=\"{Binding DataContext.Sessions.OpenDocsCommand, RelativeSource={RelativeSource AncestorType=Window}}\"",
            "the docs button command must hop to the parent Window's SessionsViewModel via RelativeSource");
    }

    [Fact]
    public void MainWindow_ActionsColumnAppearsLeftOfNameColumn()
    {
        var content = ReadMainWindowXaml();

        // The Actions column has Header="" and CanUserSort="False"; the Name
        // column has Header="Name". Both are in the DataGrid.Columns block
        // and the V1.6 layout requires Actions to appear first.
        var actionsIndex = content.IndexOf(
            "Header=\"\" Width=\"220\" CanUserSort=\"False\"",
            System.StringComparison.Ordinal);
        actionsIndex.Should().BeGreaterThan(0, "the Actions DataGridTemplateColumn must be present with the V1.6 width");

        var nameIndex = content.IndexOf(
            "Header=\"Name\" Width=\"*\" SortMemberPath=\"DisplayName\"",
            System.StringComparison.Ordinal);
        nameIndex.Should().BeGreaterThan(0, "the Name DataGridTemplateColumn must still be present");

        actionsIndex.Should().BeLessThan(nameIndex,
            "V1.6 reorders Actions to sit to the LEFT of Name");
    }

    [Fact]
    public void MainWindow_ActionsCellIsAlwaysVisible_NoHoverOpacityTrigger()
    {
        var content = ReadMainWindowXaml();

        // The V1.5 hover-reveal pattern set Opacity=0 by default and bumped
        // it to 1 on AncestorType=DataGridRow IsMouseOver. V1.6 makes the
        // actions always visible, so neither artefact may remain.
        content.Should().NotContain("<Setter Property=\"Opacity\" Value=\"0.0\" />",
            "V1.6 actions must always be visible — the V1.5 hidden-by-default opacity is removed");
        content.Should().NotContain(
            "AncestorType=DataGridRow}, Path=IsMouseOver",
            "V1.6 actions must not depend on a DataGridRow.IsMouseOver hover trigger");
    }

    [Fact]
    public void MainWindow_HasNoInlineReadmeButtonInActionsRow()
    {
        var content = ReadMainWindowXaml();

        // The inline 📄 button bound to OpenReadmeCommand was removed in
        // V1.6 to make room for the prominent 📚 Docs button. The README
        // is still reachable via the right-click context menu.
        content.Should().NotContain("Content=\"📄\"",
            "V1.6 removes the inline 📄 README button from the actions row");
    }

    [Fact]
    public void MainWindow_ContextMenu_KeepsOpenSessionReadmeEntry()
    {
        var content = ReadMainWindowXaml();

        content.Should().Contain("Header=\"Open SESSION-README\"",
            "the context menu must still expose the SESSION-README open verb");
    }

    [Fact]
    public void MainWindow_ContextMenu_AddsOpenSessionDocsEntry()
    {
        var content = ReadMainWindowXaml();

        content.Should().Contain("Open SESSION-DOCS (browser)",
            "V1.6 adds a context-menu entry that opens SESSION-DOCS.html in the browser");
        content.Should().Contain(
            "Command=\"{Binding DataContext.Sessions.OpenDocsCommand, RelativeSource={RelativeSource AncestorType=Window}}\"",
            "the SESSION-DOCS context menu entry must bind to OpenDocsCommand");
    }

    private static string ReadMainWindowXaml()
    {
        var assemblyPath = Path.GetDirectoryName(typeof(MainWindowDocsButtonTests).Assembly.Location)!;
        var probe = new DirectoryInfo(assemblyPath);
        while (probe is not null && !File.Exists(Path.Combine(probe.FullName, "CopilotSessionManager.sln")))
        {
            probe = probe.Parent;
        }
        probe.Should().NotBeNull("test must be able to find the repo root containing the sln file");
        var mainWindowXaml = Path.Combine(probe!.FullName, "src", "CopilotSessionManager", "MainWindow.xaml");
        File.Exists(mainWindowXaml).Should().BeTrue($"expected to find MainWindow.xaml at {mainWindowXaml}");
        return File.ReadAllText(mainWindowXaml);
    }
}
