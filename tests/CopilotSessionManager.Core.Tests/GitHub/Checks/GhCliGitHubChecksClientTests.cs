using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CopilotSessionManager.Core.GitHub;
using CopilotSessionManager.Core.GitHub.Checks;
using CopilotSessionManager.Core.Onboarding;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CopilotSessionManager.Core.Tests.GitHub.Checks;

/// <summary>
/// Tests <see cref="GhCliGitHubChecksClient"/> via a fake
/// <see cref="IProcessRunner"/>: verifies argument shape, JSON parsing
/// passthrough, exit-code handling (gh exits 1 on failures and 8 on
/// pending — both still produce valid JSON), and availability reporting.
/// </summary>
public class GhCliGitHubChecksClientTests
{
    [Fact]
    public async Task Success_returns_parsed_summary_and_reports_Available()
    {
        const string json = """
        [{"name":"build","state":"SUCCESS","bucket":"pass"}]
        """;
        var runner = new FakeRunner(new ProcessRunResult(0, json, ""));
        var availability = new GitHubAvailabilityProvider();
        availability.Report(GitHubAvailability.Offline, "seed");
        var sut = new GhCliGitHubChecksClient(NullLogger<GhCliGitHubChecksClient>.Instance, runner, availability);

        var result = await sut.GetChecksAsync("o/r", 42);

        result.Should().NotBeNull();
        result!.Rollup.Should().Be(PullRequestCheckRollup.Success);
        availability.Current.State.Should().Be(GitHubAvailability.Available);
        runner.Requests.Should().HaveCount(1);
        runner.Requests[0].FileName.Should().Be("gh");
        runner.Requests[0].Arguments.Should()
            .Contain("pr").And
            .Contain("checks").And
            .Contain("42").And
            .Contain("o/r");
    }

    [Fact]
    public async Task Failing_checks_exit_one_still_returns_parsed_Failure()
    {
        // gh pr checks exits 1 when at least one check has failed — the
        // JSON payload on stdout is still valid and must be honoured.
        const string json = """
        [{"name":"lint","state":"FAILURE","bucket":"fail"}]
        """;
        var runner = new FakeRunner(new ProcessRunResult(1, json, ""));
        var availability = new GitHubAvailabilityProvider();
        var sut = new GhCliGitHubChecksClient(NullLogger<GhCliGitHubChecksClient>.Instance, runner, availability);

        var result = await sut.GetChecksAsync("o/r", 42);

        result.Should().NotBeNull();
        result!.Rollup.Should().Be(PullRequestCheckRollup.Failure);
        result.AttentionCheckNames.Should().ContainSingle().Which.Should().Be("lint");
        // Exit-1 with no recognised offline/auth markers must not flip
        // availability away from Available.
        availability.Current.State.Should().Be(GitHubAvailability.Available);
    }

    [Fact]
    public async Task Pending_checks_exit_eight_still_returns_parsed_Pending()
    {
        const string json = """
        [{"name":"e2e","state":"IN_PROGRESS","bucket":"pending"}]
        """;
        var runner = new FakeRunner(new ProcessRunResult(8, json, ""));
        var availability = new GitHubAvailabilityProvider();
        var sut = new GhCliGitHubChecksClient(NullLogger<GhCliGitHubChecksClient>.Instance, runner, availability);

        var result = await sut.GetChecksAsync("o/r", 42);

        result.Should().NotBeNull();
        result!.Rollup.Should().Be(PullRequestCheckRollup.Pending);
    }

    [Fact]
    public async Task Network_error_reports_Offline_and_returns_null()
    {
        var runner = new FakeRunner(
            new ProcessRunResult(1, "", "error: could not resolve host: api.github.com"));
        var availability = new GitHubAvailabilityProvider();
        var sut = new GhCliGitHubChecksClient(NullLogger<GhCliGitHubChecksClient>.Instance, runner, availability);

        var result = await sut.GetChecksAsync("o/r", 42);

        result.Should().BeNull();
        availability.Current.State.Should().Be(GitHubAvailability.Offline);
    }

    [Fact]
    public async Task Auth_error_reports_Unauthenticated_and_returns_null()
    {
        var runner = new FakeRunner(
            new ProcessRunResult(1, "", "error: gh auth login required"));
        var availability = new GitHubAvailabilityProvider();
        var sut = new GhCliGitHubChecksClient(NullLogger<GhCliGitHubChecksClient>.Instance, runner, availability);

        var result = await sut.GetChecksAsync("o/r", 42);

        result.Should().BeNull();
        availability.Current.State.Should().Be(GitHubAvailability.Unauthenticated);
    }

    [Fact]
    public async Task Empty_or_invalid_pr_number_short_circuits()
    {
        var runner = new FakeRunner(new ProcessRunResult(0, "[]", ""));
        var sut = new GhCliGitHubChecksClient(
            NullLogger<GhCliGitHubChecksClient>.Instance, runner, availability: null,
            ghExecutable: "gh", timeout: TimeSpan.FromSeconds(15));

        (await sut.GetChecksAsync("o/r", 0)).Should().BeNull();
        (await sut.GetChecksAsync("o/r", -5)).Should().BeNull();
        (await sut.GetChecksAsync("", 42)).Should().BeNull();
        (await sut.GetChecksAsync("   ", 42)).Should().BeNull();
        runner.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Process_throws_returns_null_without_propagating()
    {
        var runner = new ThrowingRunner(new InvalidOperationException("oops"));
        var sut = new GhCliGitHubChecksClient(
            NullLogger<GhCliGitHubChecksClient>.Instance, runner, availability: null,
            ghExecutable: "gh", timeout: TimeSpan.FromSeconds(15));

        var result = await sut.GetChecksAsync("o/r", 42);

        result.Should().BeNull();
    }

    [Fact]
    public async Task Cancellation_propagates()
    {
        var runner = new CancellationRunner();
        var sut = new GhCliGitHubChecksClient(
            NullLogger<GhCliGitHubChecksClient>.Instance, runner, availability: null,
            ghExecutable: "gh", timeout: TimeSpan.FromSeconds(15));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Func<Task> act = () => sut.GetChecksAsync("o/r", 42, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Constructor_throws_on_blank_executable()
    {
        var runner = new FakeRunner(new ProcessRunResult(0, "[]", ""));
        Action act = () => new GhCliGitHubChecksClient(
            NullLogger<GhCliGitHubChecksClient>.Instance, runner, availability: null,
            ghExecutable: "   ", timeout: TimeSpan.FromSeconds(15));
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
            return Task.FromResult(new ProcessRunResult(0, "[]", ""));
        }
    }
}
