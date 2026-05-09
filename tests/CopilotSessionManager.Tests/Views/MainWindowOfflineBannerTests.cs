using System.IO;
using FluentAssertions;
using Xunit;

namespace CopilotSessionManager.Tests.Views;

/// <summary>
/// XAML resource-loading tests for the GitHub offline / unauthenticated
/// banner wired up in <c>MainWindow.xaml</c> as part of issue #84. We
/// follow the same pattern as the a11y audit tests in
/// <see cref="Accessibility.FocusVisualResourceTests"/>: read the XAML
/// source from disk and pin the binding paths so they cannot be silently
/// removed during a refactor.
/// </summary>
public class MainWindowOfflineBannerTests
{
    [Fact]
    public void MainWindow_DeclaresOfflineBannerBoundToIsGitHubOffline()
    {
        var content = ReadMainWindowXaml();

        content.Should().Contain(
            "AutomationProperties.Name=\"GitHub offline banner\"",
            "the offline banner must be present and named for screen readers");
        content.Should().Contain(
            "Visibility=\"{Binding IsGitHubOffline, Converter={StaticResource BoolToVisibility}}\"",
            "the offline banner visibility must bind to IsGitHubOffline");
    }

    [Fact]
    public void MainWindow_DeclaresUnauthenticatedBannerBoundToIsGitHubUnauthenticated()
    {
        var content = ReadMainWindowXaml();

        content.Should().Contain(
            "AutomationProperties.Name=\"GitHub unauthenticated banner\"",
            "the unauthenticated banner must be present and named for screen readers");
        content.Should().Contain(
            "Visibility=\"{Binding IsGitHubUnauthenticated, Converter={StaticResource BoolToVisibility}}\"",
            "the unauthenticated banner visibility must bind to IsGitHubUnauthenticated");
    }

    [Fact]
    public void MainWindow_BannerTextBindsToGitHubStatusMessage()
    {
        var content = ReadMainWindowXaml();

        content.Should().Contain(
            "Text=\"{Binding GitHubStatusMessage}\"",
            "banner body text must surface the live GitHub status message");
    }

    [Fact]
    public void MainWindow_BannerContainerBindsToShowGitHubBanner()
    {
        var content = ReadMainWindowXaml();

        content.Should().Contain(
            "Visibility=\"{Binding ShowGitHubBanner, Converter={StaticResource BoolToVisibility}}\"",
            "the banner StackPanel should collapse when neither offline nor unauthenticated");
    }

    [Fact]
    public void MainWindow_BannersAnnounceLiveChangesPolitely()
    {
        var content = ReadMainWindowXaml();

        // AutomationProperties.LiveSetting="Polite" makes Narrator announce
        // banner appearance/disappearance without interrupting the user.
        var politeCount = CountOccurrences(content, "AutomationProperties.LiveSetting=\"Polite\"");
        politeCount.Should().BeGreaterThanOrEqualTo(2,
            "both the offline and unauthenticated banners must opt into polite live-region announcements");
    }

    [Fact]
    public void MainWindow_PullRequestBadgeHasUnauthenticatedTooltipDataTrigger()
    {
        var content = ReadMainWindowXaml();

        content.Should().Contain(
            "DataTrigger Binding=\"{Binding DataContext.IsGitHubUnauthenticated, RelativeSource={RelativeSource AncestorType=Window}}\" Value=\"True\"",
            "PR / branch tooltips must switch on the parent window's IsGitHubUnauthenticated flag");
        content.Should().Contain(
            "gh auth login",
            "the unauthenticated tooltip must tell the user to run `gh auth login`");
    }

    [Fact]
    public void MainWindow_TodoCommentReferencesNamedBrushFollowUp()
    {
        var content = ReadMainWindowXaml();

        // The banner uses inline hex colours pending the named-brush
        // refactor tracked in #95. A TODO breadcrumb makes the cleanup
        // discoverable later.
        content.Should().Contain("#95",
            "an inline hex banner should leave a TODO referencing the named-brush refactor (#95)");
    }

    private static string ReadMainWindowXaml()
    {
        var assemblyPath = Path.GetDirectoryName(typeof(MainWindowOfflineBannerTests).Assembly.Location)!;
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

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, System.StringComparison.Ordinal)) != -1)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }
}

/// <summary>
/// Source-pinning tests for the derived <c>ShowGitHubBanner</c> helper on
/// <see cref="ViewModels.MainWindowViewModel"/>. The XAML banner container
/// binds to this property; if it is renamed or its <c>NotifyPropertyChangedFor</c>
/// wiring is removed, the banner will silently stop appearing. We pin the
/// shape of the property in source rather than re-spinning up the entire
/// MainWindowViewModel test harness, which is already exercised by
/// <c>MainWindowViewModelTests</c> for the underlying flags.
/// </summary>
public class MainWindowShowBannerStateTests
{
    [Fact]
    public void MainWindowViewModel_ExposesShowGitHubBannerDerivedProperty()
    {
        var content = ReadMainWindowViewModelSource();

        content.Should().Contain("public bool ShowGitHubBanner",
            "MainWindow.xaml binds the banner container Visibility to ShowGitHubBanner");
        content.Should().Contain("=> IsGitHubOffline || IsGitHubUnauthenticated",
            "ShowGitHubBanner must be a logical OR of the two underlying availability flags");
    }

    [Fact]
    public void MainWindowViewModel_NotifiesShowGitHubBanner_WhenEitherFlagChanges()
    {
        var content = ReadMainWindowViewModelSource();

        // Both observable backing fields must declare NotifyPropertyChangedFor
        // for ShowGitHubBanner so the XAML BoolToVisibility binding refreshes.
        var notifyCount = CountOccurrences(
            content,
            "[NotifyPropertyChangedFor(nameof(ShowGitHubBanner))]");
        notifyCount.Should().BeGreaterThanOrEqualTo(2,
            "both _isGitHubOffline and _isGitHubUnauthenticated must propagate to ShowGitHubBanner");
    }

    private static string ReadMainWindowViewModelSource()
    {
        var assemblyPath = Path.GetDirectoryName(typeof(MainWindowShowBannerStateTests).Assembly.Location)!;
        var probe = new DirectoryInfo(assemblyPath);
        while (probe is not null && !File.Exists(Path.Combine(probe.FullName, "CopilotSessionManager.sln")))
        {
            probe = probe.Parent;
        }
        probe.Should().NotBeNull("test must be able to find the repo root containing the sln file");
        var path = Path.Combine(probe!.FullName, "src", "CopilotSessionManager", "ViewModels", "MainWindowViewModel.cs");
        File.Exists(path).Should().BeTrue($"expected to find MainWindowViewModel.cs at {path}");
        return File.ReadAllText(path);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, System.StringComparison.Ordinal)) != -1)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }
}
