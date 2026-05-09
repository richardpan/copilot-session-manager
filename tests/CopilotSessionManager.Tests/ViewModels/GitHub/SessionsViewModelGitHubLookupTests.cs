using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CopilotSessionManager.Core.GitHub;
using CopilotSessionManager.Core.Models;
using CopilotSessionManager.Core.Sessions;
using CopilotSessionManager.Services;
using CopilotSessionManager.ViewModels;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CopilotSessionManager.Tests.ViewModels.GitHub;

public class SessionsViewModelGitHubLookupTests
{
    private static readonly DateTimeOffset Now = new(2026, 5, 8, 12, 0, 0, TimeSpan.Zero);

    private static Session Build(string id, string? repo, string? branch, SessionGitHubLinks? links) => new(
        Id: id,
        Cwd: null,
        Repository: repo,
        Branch: branch,
        Summary: id,
        HostType: "cli",
        CreatedAt: Now.AddMinutes(-30),
        UpdatedAt: Now.AddMinutes(-1),
        TurnCount: 1,
        Status: SessionStatus.Idle,
        CopilotVersion: CopilotVersion.Zero,
        Locks: Array.Empty<SessionLockInfo>(),
        ModelInfo: null,
        GitHubLinks: links);

    [Fact]
    public async Task Snapshot_KicksOffPullRequestLookup_PerSessionWithLinks()
    {
        var s1Links = new SessionGitHubLinks(
            "https://github.com/owner/repo",
            "https://github.com/owner/repo/tree/feature",
            PullRequest: null);
        var s2Links = new SessionGitHubLinks(
            "https://github.com/other/proj",
            "https://github.com/other/proj/tree/dev",
            PullRequest: null);
        var sessions = new[]
        {
            Build("s1", "owner/repo", "feature", s1Links),
            Build("s2", "other/proj", "dev", s2Links),
        };

        var client = new RecordingGitHubClient();
        client.Results["owner/repo|feature"] =
            new PullRequestInfo(101, "Open one", PullRequestState.Open, "https://github.com/owner/repo/pull/101");

        var vm = CreateVm(sessions, client);
        await vm.InitializeAsync();
        await client.WhenAllInvocationsComplete();

        client.Calls.Should().BeEquivalentTo(new[]
        {
            ("owner/repo", "feature"),
            ("other/proj", "dev"),
        });

        var s1Card = vm.Sessions.Single(c => c.Id == "s1");
        s1Card.PullRequestNumber.Should().Be(101);
        s1Card.PullRequestUrl.Should().Be("https://github.com/owner/repo/pull/101");

        var s2Card = vm.Sessions.Single(c => c.Id == "s2");
        s2Card.HasPullRequest.Should().BeFalse();
    }

    [Fact]
    public async Task Snapshot_SkipsLookups_WhenLinksOrBranchMissing()
    {
        // No GitHubLinks → no slug → no lookup
        var s1 = Build("s1", "owner/repo", "main", links: null);
        // Has links but missing branch → no lookup
        var s2 = Build("s2", "owner/repo", branch: null,
            new SessionGitHubLinks("https://github.com/owner/repo", BranchUrl: null, PullRequest: null));

        var client = new RecordingGitHubClient();
        var vm = CreateVm(new[] { s1, s2 }, client);
        await vm.InitializeAsync();
        await client.WhenAllInvocationsComplete();

        client.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Snapshot_NullGitHubClient_DoesNotThrow()
    {
        var links = new SessionGitHubLinks(
            "https://github.com/owner/repo",
            "https://github.com/owner/repo/tree/main",
            PullRequest: null);
        var sessions = new[] { Build("s1", "owner/repo", "main", links) };

        var vm = CreateVm(sessions, githubClient: null);
        await vm.InitializeAsync();

        vm.Sessions.Single().HasPullRequest.Should().BeFalse();
    }

    private static SessionsViewModel CreateVm(IReadOnlyList<Session> sessions, IGitHubClient? githubClient)
    {
        var disc = new InlineDiscoveryService(sessions);
        return new SessionsViewModel(
            disc,
            new InlineLabelStore(),
            new InlineReadmeService(),
            new InlineFileLauncher(),
            new InlineDispatcher(),
            new FixedTimeProvider(Now),
            modelCatalog: null,
            costCalculator: null,
            githubClient: githubClient,
            NullLogger<SessionsViewModel>.Instance);
    }

    private sealed class RecordingGitHubClient : IGitHubClient
    {
        public List<(string repo, string branch)> Calls { get; } = new();
        public Dictionary<string, PullRequestInfo?> Results { get; } = new();
        private readonly List<TaskCompletionSource> _pending = new();

        public Task<PullRequestInfo?> FindPullRequestAsync(string repoSlug, string headBranch, CancellationToken cancellationToken)
        {
            lock (Calls)
            {
                Calls.Add((repoSlug, headBranch));
            }
            var tcs = new TaskCompletionSource();
            lock (_pending)
            { _pending.Add(tcs); }

            // Run on the thread pool so callers' Task.Run continuation actually fires
            // on the dispatcher; complete synchronously.
            var key = $"{repoSlug}|{headBranch}";
            var result = Results.TryGetValue(key, out var pr) ? pr : null;
            tcs.TrySetResult();
            return Task.FromResult(result);
        }

        public async Task WhenAllInvocationsComplete()
        {
            // Spin briefly to allow the fire-and-forget Task.Run continuations
            // to execute on the thread pool + post back to the (synchronous)
            // dispatcher.
            for (var i = 0; i < 50; i++)
            {
                await Task.Yield();
                await Task.Delay(20);
            }
        }
    }

    private sealed class InlineDiscoveryService : ISessionDiscoveryService
    {
        private readonly List<Session> _current;
        public InlineDiscoveryService(IReadOnlyList<Session> initial) => _current = new List<Session>(initial);
        public IReadOnlyList<Session> CurrentSessions => _current;
#pragma warning disable CS0067 // event never raised — fake doesn't trigger
        public event EventHandler<SessionsChangedEventArgs>? SessionsChanged;
#pragma warning restore CS0067
        public Task<IReadOnlyList<Session>> ScanAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Session>>(_current);
        public Task StartWatchingAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopWatchingAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class InlineLabelStore : ISessionLabelStore
    {
#pragma warning disable CS0067
        public event EventHandler<SessionLabelChangedEventArgs>? LabelChanged;
#pragma warning restore CS0067
        public Task<SessionType> GetAsync(string sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult(SessionType.Exploratory);
        public Task<IReadOnlyDictionary<string, SessionType>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, SessionType>>(new Dictionary<string, SessionType>());
        public Task SetAsync(string sessionId, SessionType type, CancellationToken cancellationToken = default)
        {
            LabelChanged?.Invoke(this, new SessionLabelChangedEventArgs(sessionId, type));
            return Task.CompletedTask;
        }
        public Task RemoveAsync(string sessionId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class InlineReadmeService : ISessionReadmeService
    {
        public Task<string> EnsureAsync(Session session, SessionType label, CancellationToken cancellationToken = default) =>
            Task.FromResult(string.Empty);
        public string GetReadmePath(string sessionId) => $"/sessions/{sessionId}/SESSION-README.md";
        public Task AppendAsync(string sessionId, string markdown, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class InlineFileLauncher : IFileLauncher
    {
        public Task OpenAsync(string path, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class InlineDispatcher : IUiDispatcher
    {
        public void Post(Action action) => action();
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public FixedTimeProvider(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
