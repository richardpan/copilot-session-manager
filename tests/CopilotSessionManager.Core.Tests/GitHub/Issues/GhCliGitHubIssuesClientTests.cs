using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CopilotSessionManager.Core.GitHub;
using CopilotSessionManager.Core.GitHub.Issues;
using CopilotSessionManager.Core.Onboarding;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CopilotSessionManager.Core.Tests.GitHub.Issues;

/// <summary>
/// Tests <see cref="GhCliGitHubIssuesClient"/> via a fake
/// <see cref="IProcessRunner"/>. Mirrors
/// <c>GhCliGitHubChecksClientTests</c>: argument shape, JSON parsing,
/// availability reporting, and benign 404 handling for stale issues.
/// </summary>
public class GhCliGitHubIssuesClientTests
{
    private static IssueRef Ref(string ownerRepo = "o/r", int number = 42) => new(ownerRepo, number);

    [Fact]
    public async Task Success_returns_parsed_issue_and_reports_Available()
    {
        const string json = """
        {"number":42,"title":"Bug in widget","state":"OPEN","url":"https://github.com/o/r/issues/42"}
        """;
        var runner = new FakeRunner(new ProcessRunResult(0, json, ""));
        var availability = new GitHubAvailabilityProvider();
        availability.Report(GitHubAvailability.Offline, "seed");
        var sut = new GhCliGitHubIssuesClient(NullLogger<GhCliGitHubIssuesClient>.Instance, runner, availability);

        var result = await sut.GetIssueAsync(Ref());

        result.Should().NotBeNull();
        result!.Title.Should().Be("Bug in widget");
        result.State.Should().Be(IssueState.Open);
        result.Url.Should().Be("https://github.com/o/r/issues/42");
        availability.Current.State.Should().Be(GitHubAvailability.Available);

        runner.Requests.Should().HaveCount(1);
        runner.Requests[0].FileName.Should().Be("gh");
        runner.Requests[0].Arguments.Should()
            .Contain("issue").And
            .Contain("view").And
            .Contain("42").And
            .Contain("o/r");
    }

    [Fact]
    public async Task Closed_state_is_mapped()
    {
        const string json = """
        {"number":7,"title":"Done","state":"CLOSED","url":"https://github.com/o/r/issues/7"}
        """;
        var runner = new FakeRunner(new ProcessRunResult(0, json, ""));
        var sut = new GhCliGitHubIssuesClient(
            NullLogger<GhCliGitHubIssuesClient>.Instance, runner, availability: null,
            ghExecutable: "gh", timeout: TimeSpan.FromSeconds(15));

        var result = await sut.GetIssueAsync(Ref(number: 7));

        result.Should().NotBeNull();
        result!.State.Should().Be(IssueState.Closed);
    }

    [Fact]
    public async Task Unknown_state_string_is_mapped_to_Unknown()
    {
        const string json = """
        {"number":7,"title":"Mystery","state":"WEIRD","url":"https://github.com/o/r/issues/7"}
        """;
        var runner = new FakeRunner(new ProcessRunResult(0, json, ""));
        var sut = new GhCliGitHubIssuesClient(
            NullLogger<GhCliGitHubIssuesClient>.Instance, runner, availability: null,
            ghExecutable: "gh", timeout: TimeSpan.FromSeconds(15));

        var result = await sut.GetIssueAsync(Ref(number: 7));

        result.Should().NotBeNull();
        result!.State.Should().Be(IssueState.Unknown);
    }

    [Fact]
    public async Task Missing_url_falls_back_to_canonical()
    {
        const string json = """
        {"number":42,"title":"No URL","state":"OPEN"}
        """;
        var runner = new FakeRunner(new ProcessRunResult(0, json, ""));
        var sut = new GhCliGitHubIssuesClient(
            NullLogger<GhCliGitHubIssuesClient>.Instance, runner, availability: null,
            ghExecutable: "gh", timeout: TimeSpan.FromSeconds(15));

        var result = await sut.GetIssueAsync(Ref());

        result.Should().NotBeNull();
        result!.Url.Should().Be("https://github.com/o/r/issues/42");
    }

    [Fact]
    public async Task Issue_not_found_returns_null_and_does_not_flip_availability()
    {
        var runner = new FakeRunner(
            new ProcessRunResult(1, "", "GraphQL: Could not resolve to an Issuable with the number of 9999."));
        var availability = new GitHubAvailabilityProvider();
        availability.Report(GitHubAvailability.Available, userMessage: null);
        var sut = new GhCliGitHubIssuesClient(NullLogger<GhCliGitHubIssuesClient>.Instance, runner, availability);

        var result = await sut.GetIssueAsync(Ref(number: 9999));

        result.Should().BeNull();
        availability.Current.State.Should().Be(GitHubAvailability.Available);
    }

    [Fact]
    public async Task Network_error_reports_Offline_and_returns_null()
    {
        var runner = new FakeRunner(
            new ProcessRunResult(1, "", "error: could not resolve host: api.github.com"));
        var availability = new GitHubAvailabilityProvider();
        var sut = new GhCliGitHubIssuesClient(NullLogger<GhCliGitHubIssuesClient>.Instance, runner, availability);

        var result = await sut.GetIssueAsync(Ref());

        result.Should().BeNull();
        availability.Current.State.Should().Be(GitHubAvailability.Offline);
    }

    [Fact]
    public async Task Auth_error_reports_Unauthenticated_and_returns_null()
    {
        var runner = new FakeRunner(
            new ProcessRunResult(1, "", "error: gh auth login required"));
        var availability = new GitHubAvailabilityProvider();
        var sut = new GhCliGitHubIssuesClient(NullLogger<GhCliGitHubIssuesClient>.Instance, runner, availability);

        var result = await sut.GetIssueAsync(Ref());

        result.Should().BeNull();
        availability.Current.State.Should().Be(GitHubAvailability.Unauthenticated);
    }

    [Fact]
    public async Task Gh_missing_returns_null_without_propagating()
    {
        var runner = new ThrowingRunner(new InvalidOperationException("gh not on PATH"));
        var sut = new GhCliGitHubIssuesClient(
            NullLogger<GhCliGitHubIssuesClient>.Instance, runner, availability: null,
            ghExecutable: "gh", timeout: TimeSpan.FromSeconds(15));

        (await sut.GetIssueAsync(Ref())).Should().BeNull();
    }

    [Fact]
    public async Task Cancellation_propagates()
    {
        var runner = new CancellationRunner();
        var sut = new GhCliGitHubIssuesClient(
            NullLogger<GhCliGitHubIssuesClient>.Instance, runner, availability: null,
            ghExecutable: "gh", timeout: TimeSpan.FromSeconds(15));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Func<Task> act = () => sut.GetIssueAsync(Ref(), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Malformed_json_returns_null()
    {
        var runner = new FakeRunner(new ProcessRunResult(0, "{ this isn't json", ""));
        var sut = new GhCliGitHubIssuesClient(
            NullLogger<GhCliGitHubIssuesClient>.Instance, runner, availability: null,
            ghExecutable: "gh", timeout: TimeSpan.FromSeconds(15));

        (await sut.GetIssueAsync(Ref())).Should().BeNull();
    }

    [Fact]
    public async Task Empty_stdout_returns_null()
    {
        var runner = new FakeRunner(new ProcessRunResult(0, "", ""));
        var sut = new GhCliGitHubIssuesClient(
            NullLogger<GhCliGitHubIssuesClient>.Instance, runner, availability: null,
            ghExecutable: "gh", timeout: TimeSpan.FromSeconds(15));

        (await sut.GetIssueAsync(Ref())).Should().BeNull();
    }

    [Fact]
    public async Task Null_ref_throws()
    {
        var runner = new FakeRunner(new ProcessRunResult(0, "{}", ""));
        var sut = new GhCliGitHubIssuesClient(
            NullLogger<GhCliGitHubIssuesClient>.Instance, runner, availability: null,
            ghExecutable: "gh", timeout: TimeSpan.FromSeconds(15));

        Func<Task> act = () => sut.GetIssueAsync(null!);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_throws_on_blank_executable()
    {
        var runner = new FakeRunner(new ProcessRunResult(0, "{}", ""));
        Action act = () => new GhCliGitHubIssuesClient(
            NullLogger<GhCliGitHubIssuesClient>.Instance, runner, availability: null,
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
            return Task.FromResult(new ProcessRunResult(0, "{}", ""));
        }
    }
}
