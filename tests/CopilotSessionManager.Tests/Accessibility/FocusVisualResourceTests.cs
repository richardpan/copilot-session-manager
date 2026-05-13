using System.IO;
using System.Reflection;
using FluentAssertions;
using Xunit;

namespace CopilotSessionManager.Tests.Accessibility;

/// <summary>
/// A11y audit (#45): App.xaml ships a high-visibility focus visual style
/// (<c>A11yFocusVisual</c>) so keyboard focus is always obvious against
/// the Catppuccin Mocha dark theme. This test pins the resource exists
/// in the source so it can't be silently removed during a refactor.
/// </summary>
public class FocusVisualResourceTests
{
    [Fact]
    public void AppXaml_DeclaresA11yFocusVisualStyle()
    {
        var content = ReadAppXaml();

        content.Should().Contain("x:Key=\"A11yFocusVisual\"",
            "the global accessible focus visual must be defined in App.xaml");
        content.Should().Contain("Stroke=\"{DynamicResource FocusOutlineBrush}\"",
            "focus visual should follow the active named theme brush");
        content.Should().Contain("Themes/CatppuccinMocha.xaml",
            "App.xaml should merge the default named brush palette");
    }

    [Fact]
    public void AppXaml_AppliesFocusVisualToInteractiveControls()
    {
        var content = ReadAppXaml();

        // Spot-check that the focus visual is applied to the common
        // interactive control types via implicit styles.
        content.Should().Contain("TargetType=\"{x:Type Button}\"");
        content.Should().Contain("TargetType=\"{x:Type CheckBox}\"");
        content.Should().Contain("TargetType=\"{x:Type RadioButton}\"");
        content.Should().Contain("TargetType=\"{x:Type MenuItem}\"");
        content.Should().Contain("TargetType=\"{x:Type TextBox}\"");
        content.Should().Contain("FocusVisualStyle\" Value=\"{StaticResource A11yFocusVisual}\"");
    }

    private static string ReadAppXaml()
    {
        // Locate App.xaml relative to the repo root; tests run from
        // tests\CopilotSessionManager.Tests\bin\Release\net8.0-windows.
        var assemblyPath = Path.GetDirectoryName(typeof(FocusVisualResourceTests).Assembly.Location)!;
        var probe = new DirectoryInfo(assemblyPath);
        while (probe is not null && !File.Exists(Path.Combine(probe.FullName, "CopilotSessionManager.sln")))
        {
            probe = probe.Parent;
        }
        probe.Should().NotBeNull("test must be able to find the repo root containing the sln file");
        var appXaml = Path.Combine(probe!.FullName, "src", "CopilotSessionManager", "App.xaml");
        File.Exists(appXaml).Should().BeTrue($"expected to find App.xaml at {appXaml}");
        return File.ReadAllText(appXaml);
    }
}
