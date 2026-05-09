using CopilotSessionManager.Core.GitHub;
using FluentAssertions;
using Xunit;

namespace CopilotSessionManager.Core.Tests.GitHub;

public class GitHubAvailabilityProviderTests
{
    [Fact]
    public void DefaultsToAvailable()
    {
        var sut = new GitHubAvailabilityProvider(new FakeTimeProvider(SeedTime));
        sut.Current.State.Should().Be(GitHubAvailability.Available);
        sut.Current.UserMessage.Should().BeNull();
        sut.Current.DetectedAt.Should().Be(SeedTime);
    }

    [Fact]
    public void Report_FiresEventOnTransition()
    {
        var time = new FakeTimeProvider(SeedTime);
        var sut = new GitHubAvailabilityProvider(time);
        var raised = new List<GitHubAvailabilityState>();
        sut.AvailabilityChanged += (_, e) => raised.Add(e);

        time.Advance(TimeSpan.FromSeconds(5));
        sut.Report(GitHubAvailability.Offline, "no network");

        raised.Should().HaveCount(1);
        raised[0].State.Should().Be(GitHubAvailability.Offline);
        raised[0].UserMessage.Should().Be("no network");
        raised[0].DetectedAt.Should().Be(SeedTime.AddSeconds(5));
        sut.Current.Should().Be(raised[0]);
    }

    [Fact]
    public void Report_DebounceIdenticalState()
    {
        var sut = new GitHubAvailabilityProvider(new FakeTimeProvider(SeedTime));
        var count = 0;
        sut.AvailabilityChanged += (_, _) => count++;

        sut.Report(GitHubAvailability.Offline, "msg 1");
        sut.Report(GitHubAvailability.Offline, "msg 2"); // same state — ignored
        sut.Report(GitHubAvailability.Offline, "msg 3"); // same state — ignored

        count.Should().Be(1);
        // Original message preserved (debounced), so the UI doesn't flicker.
        sut.Current.UserMessage.Should().Be("msg 1");
    }

    [Fact]
    public void Report_OfflineThenAvailable_FiresAgain_AndClearsMessage()
    {
        var sut = new GitHubAvailabilityProvider(new FakeTimeProvider(SeedTime));
        var raised = new List<GitHubAvailabilityState>();
        sut.AvailabilityChanged += (_, e) => raised.Add(e);

        sut.Report(GitHubAvailability.Offline, "no network");
        sut.Report(GitHubAvailability.Available);

        raised.Should().HaveCount(2);
        raised[1].State.Should().Be(GitHubAvailability.Available);
        raised[1].UserMessage.Should().BeNull();
        sut.Current.UserMessage.Should().BeNull();
    }

    [Fact]
    public void Report_OfflineThenUnauthenticated_FiresOnEachTransition()
    {
        var sut = new GitHubAvailabilityProvider(new FakeTimeProvider(SeedTime));
        var states = new List<GitHubAvailability>();
        sut.AvailabilityChanged += (_, e) => states.Add(e.State);

        sut.Report(GitHubAvailability.Offline, "n");
        sut.Report(GitHubAvailability.Unauthenticated, "u");
        sut.Report(GitHubAvailability.Available);

        states.Should().Equal(
            GitHubAvailability.Offline,
            GitHubAvailability.Unauthenticated,
            GitHubAvailability.Available);
    }

    [Fact]
    public void DefaultConstructor_UsesSystemTimeProvider()
    {
        var sut = new GitHubAvailabilityProvider();
        sut.Current.State.Should().Be(GitHubAvailability.Available);
        sut.Current.DetectedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void NullTimeProvider_Throws()
    {
        var act = () => new GitHubAvailabilityProvider(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    private static readonly DateTimeOffset SeedTime = new(2026, 5, 9, 10, 0, 0, TimeSpan.Zero);

    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _now;
        public FakeTimeProvider(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }
}
