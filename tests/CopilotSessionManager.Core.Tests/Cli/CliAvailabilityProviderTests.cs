using CopilotSessionManager.Core.Cli;
using FluentAssertions;

namespace CopilotSessionManager.Core.Tests.Cli;

public class CliAvailabilityProviderTests
{
    [Fact]
    public void DefaultsToAvailable()
    {
        var sut = new CliAvailabilityProvider(new FakeTimeProvider(SeedTime));

        sut.Current.State.Should().Be(CliAvailability.Available);
        sut.Current.Probes.Should().BeEmpty();
        sut.Current.UserMessage.Should().BeNull();
        sut.Current.DetectedAt.Should().Be(SeedTime);
    }

    [Fact]
    public void Report_FiresEventOnTransition()
    {
        var time = new FakeTimeProvider(SeedTime);
        var sut = new CliAvailabilityProvider(time);
        var raised = new List<CliAvailabilityState>();
        sut.AvailabilityChanged += (_, e) => raised.Add(e);
        var probes = new[] { Probe("gh", "2.39.0", "2.40.0") };

        time.Advance(TimeSpan.FromSeconds(5));
        sut.Report(CliAvailability.Outdated, probes, "old gh");

        raised.Should().HaveCount(1);
        raised[0].State.Should().Be(CliAvailability.Outdated);
        raised[0].Probes.Should().Equal(probes);
        raised[0].UserMessage.Should().Be("old gh");
        raised[0].DetectedAt.Should().Be(SeedTime.AddSeconds(5));
        sut.Current.Should().Be(raised[0]);
    }

    [Fact]
    public void Report_DebouncesIdenticalStateAndProbes()
    {
        var sut = new CliAvailabilityProvider(new FakeTimeProvider(SeedTime));
        var probes = new[] { Probe("gh", "2.39.0", "2.40.0") };
        var count = 0;
        sut.AvailabilityChanged += (_, _) => count++;

        sut.Report(CliAvailability.Outdated, probes, "msg 1");
        sut.Report(CliAvailability.Outdated, probes, "msg 2");

        count.Should().Be(1);
        sut.Current.UserMessage.Should().Be("msg 1");
    }

    [Fact]
    public void Report_SameStateWithDifferentProbes_FiresAgain()
    {
        var sut = new CliAvailabilityProvider(new FakeTimeProvider(SeedTime));
        var count = 0;
        sut.AvailabilityChanged += (_, _) => count++;

        sut.Report(CliAvailability.Outdated, new[] { Probe("gh", "2.39.0", "2.40.0") }, "old");
        sut.Report(CliAvailability.Outdated, new[] { Probe("gh", "2.38.0", "2.40.0") }, "older");

        count.Should().Be(2);
        sut.Current.Probes[0].Detected.Should().Be(new Version(2, 38, 0));
    }

    [Fact]
    public void Report_OutdatedThenAvailable_FiresAgainAndClearsMessage()
    {
        var sut = new CliAvailabilityProvider(new FakeTimeProvider(SeedTime));
        var states = new List<CliAvailability>();
        sut.AvailabilityChanged += (_, e) => states.Add(e.State);

        sut.Report(CliAvailability.Outdated, new[] { Probe("gh", "2.39.0", "2.40.0") }, "old");
        sut.Report(CliAvailability.Available, new[] { Probe("gh", "2.40.0", "2.40.0", outdated: false) });

        states.Should().Equal(CliAvailability.Outdated, CliAvailability.Available);
        sut.Current.UserMessage.Should().BeNull();
    }

    [Fact]
    public void NullTimeProvider_Throws()
    {
        var act = () => new CliAvailabilityProvider(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    private static CliVersionInfo Probe(string cli, string detected, string minimum, bool outdated = true) =>
        new(cli, new Version(detected), new Version(minimum), outdated, detected);

    private static readonly DateTimeOffset SeedTime = new(2026, 5, 9, 10, 0, 0, TimeSpan.Zero);

    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _now;
        public FakeTimeProvider(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }
}
