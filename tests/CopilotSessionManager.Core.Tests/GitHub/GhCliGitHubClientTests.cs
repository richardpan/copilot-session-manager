using CopilotSessionManager.Core.GitHub;
using CopilotSessionManager.Core.Onboarding;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CopilotSessionManager.Core.Tests.GitHub;

public class GhCliGitHubClientTests
{
    [Fact]
    public async Task Success_ReportsAvailable_AndReturnsParsedPr()
    {
        const string json = """
        [{"number":42,"title":"Add thing","state":"OPEN","isDraft":false,"url":"https://github.com/o/r/pull/42"}]
        """;
        var runner = new FakeRunner(new ProcessRunResult(0, json, ""));
        var availability = new GitHubAvailabilityProvider();
        // Force a non-available baseline so the success transitions back.
        availability.Report(GitHubAvailability.Offline, "seed");
        var sut = new GhCliGitHubClient(NullLogger<GhCliGitHubClient>.Instance, runner, availability);

        var pr = await sut.FindPullRequestAsync("o/r", "main");

        pr.Should().NotBeNull();
        pr!.Number.Should().Be(42);
        availability.Current.State.Should().Be(GitHubAvailability.Available);
        runner.Requests.Should().HaveCount(1);
        runner.Requests[0].FileName.Should().Be("gh");
        runner.Requests[0].Arguments.Should().Contain("pr").And.Contain("list").And.Contain("o/r");
    }

    [Fact]
    public async Task NetworkError_ReportsOffline_AndReturnsNull()
    {
        var runner = new FakeRunner(
            new ProcessRunResult(1, "", "error: could not resolve host: api.github.com"));
        var availability = new GitHubAvailabilityProvider();
        var sut = new GhCliGitHubClient(NullLogger<GhCliGitHubClient>.Instance, runner, availability);

        var pr = await sut.FindPullRequestAsync("o/r", "main");

        pr.Should().BeNull();
        availability.Current.State.Should().Be(GitHubAvailability.Offline);
        availability.Current.UserMessage.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task AuthError_ReportsUnauthenticated_AndReturnsNull()
    {
        var runner = new FakeRunner(
            new ProcessRunResult(1, "", "error: not authenticated. Run gh auth login."));
        var availability = new GitHubAvailabilityProvider();
        var sut = new GhCliGitHubClient(NullLogger<GhCliGitHubClient>.Instance, runner, availability);

        var pr = await sut.FindPullRequestAsync("o/r", "main");

        pr.Should().BeNull();
        availability.Current.State.Should().Be(GitHubAvailability.Unauthenticated);
        availability.Current.UserMessage.Should().Contain("gh auth login");
    }

    [Fact]
    public async Task UnknownNonZeroExit_DoesNotChangeAvailability()
    {
        var runner = new FakeRunner(new ProcessRunResult(1, "[]", "no PR found"));
        var availability = new GitHubAvailabilityProvider();
        availability.Report(GitHubAvailability.Offline, "seed"); // baseline
        var sut = new GhCliGitHubClient(NullLogger<GhCliGitHubClient>.Instance, runner, availability);

        var pr = await sut.FindPullRequestAsync("o/r", "main");

        pr.Should().BeNull();
        // Should remain Offline — we should NOT silently flip to Available
        // on an ambiguous non-zero exit.
        availability.Current.State.Should().Be(GitHubAvailability.Offline);
    }

    [Fact]
    public async Task SuccessAfterFailure_TransitionsBackToAvailable()
    {
        var runner = new ScriptedRunner(
            new ProcessRunResult(1, "", "could not resolve host"),
            new ProcessRunResult(0, "[]", ""));
        var availability = new GitHubAvailabilityProvider();
        var sut = new GhCliGitHubClient(NullLogger<GhCliGitHubClient>.Instance, runner, availability);
        var raised = new List<GitHubAvailabilityState>();
        availability.AvailabilityChanged += (_, e) => raised.Add(e);

        await sut.FindPullRequestAsync("o/r", "main");
        availability.Current.State.Should().Be(GitHubAvailability.Offline);

        await sut.FindPullRequestAsync("o/r", "main");
        availability.Current.State.Should().Be(GitHubAvailability.Available);

        raised.Select(r => r.State).Should().Equal(
            GitHubAvailability.Offline,
            GitHubAvailability.Available);
    }

    [Fact]
    public async Task TwoConsecutiveOfflineFailures_FireEventOnlyOnce()
    {
        var runner = new FakeRunner(
            new ProcessRunResult(1, "", "could not resolve host"));
        var availability = new GitHubAvailabilityProvider();
        var raisedCount = 0;
        availability.AvailabilityChanged += (_, _) => raisedCount++;
        var sut = new GhCliGitHubClient(NullLogger<GhCliGitHubClient>.Instance, runner, availability);

        await sut.FindPullRequestAsync("o/r", "main");
        await sut.FindPullRequestAsync("o/r", "main");
        await sut.FindPullRequestAsync("o/r", "main");

        raisedCount.Should().Be(1);
    }

    [Fact]
    public async Task EmptyArgs_ReturnNullWithoutInvokingRunner()
    {
        var runner = new FakeRunner(new ProcessRunResult(0, "[]", ""));
        var sut = new GhCliGitHubClient(NullLogger<GhCliGitHubClient>.Instance, runner, new GitHubAvailabilityProvider());

        (await sut.FindPullRequestAsync("", "main")).Should().BeNull();
        (await sut.FindPullRequestAsync("o/r", "")).Should().BeNull();

        runner.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task RunnerThrows_DoesNotCrash_AndDoesNotChangeAvailability()
    {
        var runner = new ThrowingRunner(new InvalidOperationException("boom"));
        var availability = new GitHubAvailabilityProvider();
        var sut = new GhCliGitHubClient(NullLogger<GhCliGitHubClient>.Instance, runner, availability);

        var pr = await sut.FindPullRequestAsync("o/r", "main");
        pr.Should().BeNull();
        availability.Current.State.Should().Be(GitHubAvailability.Available);
    }

    [Fact]
    public async Task NullAvailability_StillReturnsResultsAndDoesNotThrow()
    {
        var runner = new FakeRunner(new ProcessRunResult(1, "", "could not resolve host"));
        var sut = new GhCliGitHubClient(
            NullLogger<GhCliGitHubClient>.Instance,
            runner,
            availability: null,
            ghExecutable: "gh",
            timeout: TimeSpan.FromSeconds(5));

        var pr = await sut.FindPullRequestAsync("o/r", "main");
        pr.Should().BeNull();
    }

    [Fact]
    public async Task UserCancellation_PropagatesAsOperationCanceled()
    {
        var runner = new CancellationRunner();
        var sut = new GhCliGitHubClient(NullLogger<GhCliGitHubClient>.Instance, runner, new GitHubAvailabilityProvider());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await sut.FindPullRequestAsync("o/r", "main", cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void NullLogger_Throws()
    {
        var act = () => new GhCliGitHubClient(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void NullRunner_Throws()
    {
        var act = () => new GhCliGitHubClient(
            NullLogger<GhCliGitHubClient>.Instance,
            runner: null!,
            availability: new GitHubAvailabilityProvider());
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void EmptyExecutable_Throws()
    {
        var act = () => new GhCliGitHubClient(
            NullLogger<GhCliGitHubClient>.Instance,
            new FakeRunner(new ProcessRunResult(0, "", "")),
            availability: null,
            ghExecutable: "",
            timeout: TimeSpan.FromSeconds(5));
        act.Should().Throw<ArgumentException>();
    }

    private sealed class FakeRunner : IProcessRunner
    {
        private readonly ProcessRunResult _result;
        public List<ProcessRunRequest> Requests { get; } = new();
        public FakeRunner(ProcessRunResult result) => _result = result;
        public Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(_result);
        }
    }

    private sealed class ScriptedRunner : IProcessRunner
    {
        private readonly Queue<ProcessRunResult> _results;
        public ScriptedRunner(params ProcessRunResult[] results) => _results = new Queue<ProcessRunResult>(results);
        public Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(_results.Dequeue());
    }

    private sealed class ThrowingRunner : IProcessRunner
    {
        private readonly Exception _ex;
        public ThrowingRunner(Exception ex) => _ex = ex;
        public Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken cancellationToken = default) =>
            throw _ex;
    }

    private sealed class CancellationRunner : IProcessRunner
    {
        public Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new ProcessRunResult(0, "", ""));
        }
    }
}
