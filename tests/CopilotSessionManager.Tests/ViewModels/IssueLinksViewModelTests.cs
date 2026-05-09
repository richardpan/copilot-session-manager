using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CopilotSessionManager.Core.GitHub.Issues;
using CopilotSessionManager.Core.GitHub.Storage;
using CopilotSessionManager.Services;
using CopilotSessionManager.ViewModels;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CopilotSessionManager.Tests.ViewModels;

public class IssueLinksViewModelTests
{
    private const string SessionId = "0123456789abcdef0123456789abcdef01234567";
    private const string DefaultRepo = "octo/widgets";

    [Fact]
    public async Task AddIssueCommand_ParsesAndAppendsBadge()
    {
        var (vm, store, fetcher) = CreateSut(dialogReturns: new IssueRef("octo/widgets", 1));

        await vm.AddIssueCommand.ExecuteAsync(null);

        vm.Links.Should().ContainSingle();
        vm.Links[0].Ref.OwnerRepo.Should().Be("octo/widgets");
        vm.Links[0].Ref.Number.Should().Be(1);
        store.Adds.Should().ContainSingle().Which.Item2.Number.Should().Be(1);
        await fetcher.WaitForRequestsAsync(1);
        fetcher.Requests.Should().ContainSingle().Which.Number.Should().Be(1);
    }

    [Fact]
    public async Task AddIssueCommand_DuplicateRef_DoesNotAddTwice()
    {
        var (vm, store, _) = CreateSut(dialogReturns: new IssueRef("octo/widgets", 1));

        await vm.AddIssueCommand.ExecuteAsync(null);
        await vm.AddIssueCommand.ExecuteAsync(null);

        vm.Links.Should().ContainSingle();
        store.Adds.Should().ContainSingle();
        vm.StatusMessage.Should().Contain("already linked");
    }

    [Fact]
    public async Task AddIssueCommand_DialogReturnsNull_NoBadge()
    {
        var (vm, store, _) = CreateSut(dialogReturns: null);

        await vm.AddIssueCommand.ExecuteAsync(null);

        vm.Links.Should().BeEmpty();
        store.Adds.Should().BeEmpty();
    }

    [Fact]
    public async Task RemoveCommand_DropsBadgeAndCallsStore()
    {
        var (vm, store, _) = CreateSut(dialogReturns: new IssueRef("octo/widgets", 5));
        await vm.AddIssueCommand.ExecuteAsync(null);

        var badge = vm.Links.Single();
        await ((CommunityToolkit.Mvvm.Input.AsyncRelayCommand)badge.RemoveCommand).ExecuteAsync(null);

        vm.Links.Should().BeEmpty();
        store.Removes.Should().ContainSingle().Which.Item2.Number.Should().Be(5);
    }

    [Fact]
    public async Task LoadAsync_HydratesPreviouslyStoredRefs()
    {
        var (vm, store, fetcher) = CreateSut(dialogReturns: null);
        store.Seed(SessionId, new[] { "octo/widgets#10", "acme/tools#11" });

        await vm.LoadAsync();

        vm.Links.Should().HaveCount(2);
        vm.Links.Select(l => l.Ref.Number).Should().BeEquivalentTo(new[] { 10, 11 });
        await fetcher.WaitForRequestsAsync(2);
        fetcher.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task FetchedMetadata_AppliedToBadge()
    {
        var (vm, _, fetcher) = CreateSut(dialogReturns: new IssueRef("octo/widgets", 3));
        fetcher.SetResponse(new IssueInfo(new IssueRef("octo/widgets", 3), "Bug bash", IssueState.Open,
            "https://github.com/octo/widgets/issues/3"));

        await vm.AddIssueCommand.ExecuteAsync(null);
        await fetcher.WaitForRequestsAsync(1);
        await Task.Delay(50); // allow dispatcher (synchronous) to run apply

        vm.Links.Single().State.Should().Be(IssueState.Open);
        vm.Links.Single().Title.Should().Be("Bug bash");
    }

    [Fact]
    public async Task LoadAsync_NullStore_DoesNothing()
    {
        var vm = new IssueLinksViewModel(
            SessionId, DefaultRepo,
            issuesClient: null,
            linksStore: null,
            fileLauncher: null,
            dispatcher: new SessionsViewModelTests.SyncDispatcher(),
            showAddDialog: _ => null,
            logger: null);

        await vm.LoadAsync();

        vm.Links.Should().BeEmpty();
    }

    [Fact]
    public void Constructor_Validates()
    {
        Action act = () => new IssueLinksViewModel(
            sessionId: " ",
            defaultOwnerRepo: null,
            issuesClient: null,
            linksStore: null,
            fileLauncher: null,
            dispatcher: new SessionsViewModelTests.SyncDispatcher(),
            showAddDialog: _ => null,
            logger: null);

        act.Should().Throw<ArgumentException>();
    }

    private static (IssueLinksViewModel vm, FakeStore store, FakeIssuesClient fetcher) CreateSut(IssueRef? dialogReturns)
    {
        var store = new FakeStore();
        var fetcher = new FakeIssuesClient();
        var vm = new IssueLinksViewModel(
            SessionId,
            DefaultRepo,
            fetcher,
            store,
            new SessionsViewModelTests.FakeFileLauncher(),
            new SessionsViewModelTests.SyncDispatcher(),
            _ => dialogReturns,
            new Microsoft.Extensions.Logging.Logger<IssueLinksViewModel>(NullLoggerFactory.Instance));
        return (vm, store, fetcher);
    }

    private sealed class FakeIssuesClient : IGitHubIssuesClient
    {
        private readonly List<IssueRef> _requests = new();
        private readonly object _lock = new();
        private IssueInfo? _response;
        private TaskCompletionSource<bool>? _waiter;
        private int _expected;

        public IReadOnlyList<IssueRef> Requests
        {
            get
            {
                lock (_lock)
                    return _requests.ToArray();
            }
        }

        public void SetResponse(IssueInfo info) => _response = info;

        public Task<IssueInfo?> GetIssueAsync(IssueRef issueRef, CancellationToken cancellationToken = default)
        {
            lock (_lock)
            {
                _requests.Add(issueRef);
                if (_waiter is not null && _requests.Count >= _expected)
                {
                    _waiter.TrySetResult(true);
                }
            }
            return Task.FromResult(_response);
        }

        public async Task WaitForRequestsAsync(int count, int timeoutMs = 2000)
        {
            TaskCompletionSource<bool> tcs;
            lock (_lock)
            {
                if (_requests.Count >= count)
                    return;
                _waiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _expected = count;
                tcs = _waiter;
            }
            var done = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs));
            done.Should().Be(tcs.Task);
        }
    }

    private sealed class FakeStore : ISessionGitHubLinksStore
    {
        public List<(string SessionId, IssueRef Ref)> Adds { get; } = new();
        public List<(string SessionId, IssueRef Ref)> Removes { get; } = new();
        private readonly Dictionary<string, List<string>> _seeded = new(StringComparer.OrdinalIgnoreCase);

        public void Seed(string sessionId, IEnumerable<string> refs) => _seeded[sessionId] = refs.ToList();

        public Task<SessionGitHubLinkOverrides?> GetAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            if (!_seeded.TryGetValue(sessionId, out var list))
            {
                return Task.FromResult<SessionGitHubLinkOverrides?>(null);
            }
            var overrides = new SessionGitHubLinkOverrides(null, null, null)
            {
                IssueRefs = list,
            };
            return Task.FromResult<SessionGitHubLinkOverrides?>(overrides);
        }

        public Task SetAsync(string sessionId, SessionGitHubLinkOverrides overrides, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task ClearAsync(string sessionId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task AddIssueRefAsync(string sessionId, IssueRef issueRef, CancellationToken cancellationToken = default)
        {
            Adds.Add((sessionId, issueRef));
            return Task.CompletedTask;
        }

        public Task RemoveIssueRefAsync(string sessionId, IssueRef issueRef, CancellationToken cancellationToken = default)
        {
            Removes.Add((sessionId, issueRef));
            return Task.CompletedTask;
        }
    }
}
