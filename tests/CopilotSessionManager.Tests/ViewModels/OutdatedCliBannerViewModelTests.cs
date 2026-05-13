using CopilotSessionManager.Core.Cli;
using CopilotSessionManager.ViewModels;
using FluentAssertions;

namespace CopilotSessionManager.Tests.ViewModels;

public class OutdatedCliBannerViewModelTests
{
    [Fact]
    public void AvailableState_IsHidden()
    {
        var provider = new CliAvailabilityProvider();
        var sut = new OutdatedCliBannerViewModel(provider);

        sut.IsVisible.Should().BeFalse();
        sut.Headline.Should().BeEmpty();
        sut.UpgradeInstructions.Should().BeEmpty();
    }

    [Fact]
    public void OutdatedGh_ShowsHeadlineAndUpgradeCommand()
    {
        var provider = new CliAvailabilityProvider();
        var sut = new OutdatedCliBannerViewModel(provider);

        provider.Report(CliAvailability.Outdated, new[] { Probe("gh", "2.39.0", "2.40.0") }, "old gh");

        sut.IsVisible.Should().BeTrue();
        sut.Headline.Should().Be("GitHub CLI 2.39.0 is older than the minimum supported (2.40.0).");
        sut.UpgradeInstructions.Should().Contain("winget upgrade GitHub.cli");
    }

    [Fact]
    public void OutdatedCopilot_ShowsExtensionUpgradeCommand()
    {
        var provider = new CliAvailabilityProvider();
        var sut = new OutdatedCliBannerViewModel(provider);

        provider.Report(CliAvailability.Outdated, new[] { Probe("copilot", "0.9.0", "1.0.0") }, "old copilot");

        sut.IsVisible.Should().BeTrue();
        sut.Headline.Should().Contain("Copilot CLI 0.9.0");
        sut.UpgradeInstructions.Should().Contain("gh extension install github/gh-copilot");
        sut.UpgradeInstructions.Should().Contain("gh extension upgrade gh-copilot");
    }

    [Fact]
    public void Dismiss_HidesCurrentFingerprint()
    {
        var provider = new CliAvailabilityProvider();
        var sut = new OutdatedCliBannerViewModel(provider);
        provider.Report(CliAvailability.Outdated, new[] { Probe("gh", "2.39.0", "2.40.0") }, "old gh");

        sut.DismissCommand.Execute(null);

        sut.IsVisible.Should().BeFalse();
    }

    [Fact]
    public void SameOutdatedFingerprint_AfterDismiss_StaysHidden()
    {
        var provider = new CliAvailabilityProvider();
        var sut = new OutdatedCliBannerViewModel(provider);
        var probes = new[] { Probe("gh", "2.39.0", "2.40.0") };
        provider.Report(CliAvailability.Outdated, probes, "old gh");
        sut.DismissCommand.Execute(null);

        provider.Report(CliAvailability.Available, Array.Empty<CliVersionInfo>());
        provider.Report(CliAvailability.Outdated, probes, "old gh again");

        sut.IsVisible.Should().BeFalse();
    }

    [Fact]
    public void NewOutdatedFingerprint_AfterDismiss_Reappears()
    {
        var provider = new CliAvailabilityProvider();
        var sut = new OutdatedCliBannerViewModel(provider);
        provider.Report(CliAvailability.Outdated, new[] { Probe("gh", "2.39.0", "2.40.0") }, "old gh");
        sut.DismissCommand.Execute(null);

        provider.Report(CliAvailability.Outdated, new[] { Probe("gh", "2.38.0", "2.40.0") }, "older gh");

        sut.IsVisible.Should().BeTrue();
        sut.Headline.Should().Contain("2.38.0");
    }

    [Fact]
    public void NotInstalled_ShowsMissingMessage()
    {
        var provider = new CliAvailabilityProvider();
        var sut = new OutdatedCliBannerViewModel(provider);

        provider.Report(CliAvailability.NotInstalled, new[] { Probe("gh", "0.0.0", "2.40.0", "executable not found") }, "missing");

        sut.IsVisible.Should().BeTrue();
        sut.Headline.Should().Be("GitHub CLI is not installed or could not be probed.");
        sut.UpgradeInstructions.Should().Contain("not detected");
        sut.UpgradeInstructions.Should().Contain("winget upgrade GitHub.cli");
    }

    [Fact]
    public void MultipleOutdatedCliTools_ShowCombinedInstructions()
    {
        var provider = new CliAvailabilityProvider();
        var sut = new OutdatedCliBannerViewModel(provider);
        var probes = new[]
        {
            Probe("gh", "2.39.0", "2.40.0"),
            Probe("copilot", "0.9.0", "1.0.0"),
        };

        provider.Report(CliAvailability.Outdated, probes, "old tools");

        sut.IsVisible.Should().BeTrue();
        sut.Headline.Should().Be("Some CLI tools are older than the minimum supported versions.");
        sut.UpgradeInstructions.Should().Contain("GitHub CLI");
        sut.UpgradeInstructions.Should().Contain("Copilot CLI");
        sut.UpgradeInstructions.Should().Contain("winget upgrade GitHub.cli");
        sut.UpgradeInstructions.Should().Contain("gh extension upgrade gh-copilot");
    }

    [Fact]
    public void RecoveryToAvailable_HidesBanner()
    {
        var provider = new CliAvailabilityProvider();
        var sut = new OutdatedCliBannerViewModel(provider);
        provider.Report(CliAvailability.Outdated, new[] { Probe("gh", "2.39.0", "2.40.0") }, "old gh");

        provider.Report(CliAvailability.Available, new[] { Probe("gh", "2.40.0", "2.40.0", outdated: false) });

        sut.IsVisible.Should().BeFalse();
        sut.Headline.Should().BeEmpty();
        sut.UpgradeInstructions.Should().BeEmpty();
    }

    [Fact]
    public void Refresh_RecomputesFromProviderCurrentState()
    {
        var provider = new CliAvailabilityProvider();
        provider.Report(CliAvailability.Outdated, new[] { Probe("copilot", "0.9.0", "1.0.0") }, "old copilot");
        var sut = new OutdatedCliBannerViewModel(provider);

        sut.IsVisible = false;
        sut.Refresh();

        sut.IsVisible.Should().BeTrue();
        sut.Headline.Should().Contain("Copilot CLI");
    }

    private static CliVersionInfo Probe(
        string cli,
        string detected,
        string minimum,
        string rawVersionLine = "raw",
        bool outdated = true) =>
        new(cli, new Version(detected), new Version(minimum), outdated, rawVersionLine);
}
