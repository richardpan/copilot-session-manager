using System.IO;
using System.Linq;
using FluentAssertions;
using Xunit;

namespace CopilotSessionManager.Tests.Lifecycle;

/// <summary>
/// Regression tests for #102: a fresh-install user closing the modal
/// first-run Onboarding window caused WPF's default
/// <c>ShutdownMode=OnLastWindowClose</c> to immediately tear down the
/// application after <c>MainWindow.Show()</c> ran (race between the
/// queued shutdown and the new window appearing).
///
/// The fix relies on two pieces working together:
/// 1. <c>App.xaml</c> declares <c>ShutdownMode="OnExplicitShutdown"</c>
///    so closing the Onboarding dialog cannot trigger app shutdown.
/// 2. <c>App.xaml.cs</c> attaches a <c>Closed</c> handler on MainWindow
///    that calls <see cref="System.Windows.Application.Shutdown()"/>
///    explicitly, so honest closes still quit the process.
///
/// These tests pin both halves in source so a future refactor can't
/// silently re-introduce the bug.
/// </summary>
public class OnboardingShutdownRegressionTests
{
    [Fact]
    public void AppXaml_DeclaresOnExplicitShutdownMode()
    {
        var content = ReadRepoFile("src", "CopilotSessionManager", "App.xaml");

        content.Should().Contain("ShutdownMode=\"OnExplicitShutdown\"",
            "App.xaml must declare OnExplicitShutdown so the modal first-run " +
            "Onboarding window closing does not race MainWindow.Show() into a " +
            "premature app shutdown (#102).");
    }

    [Fact]
    public void AppCodeBehind_AttachesClosedHandlerOnMainWindow()
    {
        var content = ReadRepoFile("src", "CopilotSessionManager", "App.xaml.cs");

        content.Should().Contain("mainWindow.Closed += OnMainWindowClosed",
            "App.xaml.cs must wire a Closed handler on the MainWindow so " +
            "honest closes still call Application.Shutdown — required because " +
            "ShutdownMode is OnExplicitShutdown (#102).");
    }

    [Fact]
    public void AppCodeBehind_OnMainWindowClosed_CallsShutdown()
    {
        var content = ReadRepoFile("src", "CopilotSessionManager", "App.xaml.cs");

        content.Should().Contain("private void OnMainWindowClosed(",
            "App.xaml.cs must declare the OnMainWindowClosed handler.");
        content.Should().MatchRegex(
            @"OnMainWindowClosed\([^)]*\)[\s\S]*?Shutdown\(0\)",
            "OnMainWindowClosed must call Shutdown(0) so closing the main " +
            "window actually exits the process under OnExplicitShutdown (#102).");
    }

    private static string ReadRepoFile(params string[] segments)
    {
        var assemblyPath = Path.GetDirectoryName(typeof(OnboardingShutdownRegressionTests).Assembly.Location)!;
        var probe = new DirectoryInfo(assemblyPath);
        while (probe is not null && !File.Exists(Path.Combine(probe.FullName, "CopilotSessionManager.sln")))
        {
            probe = probe.Parent;
        }
        probe.Should().NotBeNull("test must be able to find the repo root containing the sln file");

        var fullPath = Path.Combine(new[] { probe!.FullName }.Concat(segments).ToArray());
        File.Exists(fullPath).Should().BeTrue($"expected to find file at {fullPath}");
        return File.ReadAllText(fullPath);
    }
}
