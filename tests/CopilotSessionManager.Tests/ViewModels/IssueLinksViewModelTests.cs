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

    private static (IssueLinksViewModel vm, FakeStore store, FakeIssuesClient fetcher, FakeReadmeRefsProvider readme) CreateSutWithReadme(
        IssueRef? dialogReturns,
        params IssueRef[] readmeRefs)
    {
        var store = new FakeStore();
        var fetcher = new FakeIssuesClient();
        var readme = new FakeReadmeRefsProvider();
        readme.Set(readmeRefs);
        var vm = new IssueLinksViewModel(
            SessionId,
            DefaultRepo,
            fetcher,
            store,
            new SessionsViewModelTests.FakeFileLauncher(),
            new SessionsViewModelTests.SyncDispatcher(),
            _ => dialogReturns,
            new Microsoft.Extensions.Logging.Logger<IssueLinksViewModel>(NullLoggerFactory.Instance),
            readme);
        return (vm, store, fetcher, readme);
    }

    [Fact]
    public async Task LoadAsync_NoReadmeProvider_OnlyHydratesManualRefs()
    {
        var (vm, store, _) = CreateSut(dialogReturns: null);
        store.Seed(SessionId, new[] { "octo/widgets#10" });

        await vm.LoadAsync();

        vm.Links.Should().ContainSingle();
        vm.Links[0].Origin.Should().Be(IssueLinkOrigin.Manual);
    }

    [Fact]
    public async Task LoadAsync_WithReadme_AppendsParsedRefsAfterManual()
    {
        var (vm, store, _, _) = CreateSutWithReadme(
            dialogReturns: null,
            readmeRefs: new[]
            {
                new IssueRef("octo/widgets", 50),
                new IssueRef("acme/tools", 51),
            });
        store.Seed(SessionId, new[] { "octo/widgets#1" });

        await vm.LoadAsync();

        vm.Links.Should().HaveCount(3);
        vm.Links[0].Ref.Number.Should().Be(1);
        vm.Links[0].Origin.Should().Be(IssueLinkOrigin.Manual);
        vm.Links[1].Ref.Number.Should().Be(50);
        vm.Links[1].Origin.Should().Be(IssueLinkOrigin.ParsedFromReadme);
        vm.Links[2].Ref.Number.Should().Be(51);
        vm.Links[2].Origin.Should().Be(IssueLinkOrigin.ParsedFromReadme);
    }

    [Fact]
    public async Task LoadAsync_WithReadmeNoManual_AppendsParsedRefs()
    {
        var (vm, _, _, _) = CreateSutWithReadme(
            dialogReturns: null,
            readmeRefs: new[] { new IssueRef("octo/widgets", 99) });

        await vm.LoadAsync();

        vm.Links.Should().ContainSingle();
        vm.Links[0].Ref.Number.Should().Be(99);
        vm.Links[0].Origin.Should().Be(IssueLinkOrigin.ParsedFromReadme);
    }

    [Fact]
    public async Task LoadAsync_DedupBetweenManualAndParsed_ManualWins()
    {
        var sharedRef = new IssueRef("octo/widgets", 5);
        var (vm, store, _, _) = CreateSutWithReadme(
            dialogReturns: null,
            readmeRefs: new[] { sharedRef });
        store.Seed(SessionId, new[] { "octo/widgets#5" });

        await vm.LoadAsync();

        vm.Links.Should().ContainSingle();
        vm.Links[0].Origin.Should().Be(IssueLinkOrigin.Manual);
    }

    [Fact]
    public async Task ParsedRef_RemoveCommand_IsDisabled()
    {
        var (vm, _, _, _) = CreateSutWithReadme(
            dialogReturns: null,
            readmeRefs: new[] { new IssueRef("octo/widgets", 7) });

        await vm.LoadAsync();

        var badge = vm.Links.Single();
        badge.Origin.Should().Be(IssueLinkOrigin.ParsedFromReadme);
        badge.CanRemove().Should().BeFalse();
        badge.RemoveCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public async Task ManualRef_RemoveCommand_IsEnabled()
    {
        var (vm, _, _) = CreateSut(dialogReturns: new IssueRef("octo/widgets", 7));

        await vm.AddIssueCommand.ExecuteAsync(null);
        var badge = vm.Links.Single();

        badge.Origin.Should().Be(IssueLinkOrigin.Manual);
        badge.CanRemove().Should().BeTrue();
        badge.RemoveCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public async Task ParsedRef_TooltipIncludesParsedSuffix()
    {
        var (vm, _, _, _) = CreateSutWithReadme(
            dialogReturns: null,
            readmeRefs: new[] { new IssueRef("octo/widgets", 7) });

        await vm.LoadAsync();

        vm.Links.Single().Tooltip.Should().Contain("(parsed from README)");
    }

    [Fact]
    public async Task ManualRef_TooltipDoesNotIncludeParsedSuffix()
    {
        var (vm, _, _) = CreateSut(dialogReturns: new IssueRef("octo/widgets", 7));

        await vm.AddIssueCommand.ExecuteAsync(null);

        vm.Links.Single().Tooltip.Should().NotContain("(parsed from README)");
    }

    [Fact]
    public async Task RefreshParsedRefsAsync_AddsNewRefs()
    {
        var (vm, _, _, readme) = CreateSutWithReadme(
            dialogReturns: null,
            readmeRefs: new[] { new IssueRef("octo/widgets", 1) });

        await vm.LoadAsync();
        vm.Links.Should().HaveCount(1);

        readme.Set(new[]
        {
            new IssueRef("octo/widgets", 1),
            new IssueRef("octo/widgets", 2),
        });
        await vm.RefreshParsedRefsAsync();

        vm.Links.Select(l => l.Ref.Number).Should().BeEquivalentTo(new[] { 1, 2 });
        vm.Links.Should().OnlyContain(l => l.Origin == IssueLinkOrigin.ParsedFromReadme);
    }

    [Fact]
    public async Task RefreshParsedRefsAsync_DropsRefsRemovedFromReadme()
    {
        var (vm, _, _, readme) = CreateSutWithReadme(
            dialogReturns: null,
            readmeRefs: new[]
            {
                new IssueRef("octo/widgets", 1),
                new IssueRef("octo/widgets", 2),
            });

        await vm.LoadAsync();
        vm.Links.Should().HaveCount(2);

        readme.Set(new[] { new IssueRef("octo/widgets", 2) });
        await vm.RefreshParsedRefsAsync();

        vm.Links.Should().ContainSingle();
        vm.Links[0].Ref.Number.Should().Be(2);
    }

    [Fact]
    public async Task RefreshParsedRefsAsync_DoesNotRemoveManualRefs()
    {
        var (vm, store, _, readme) = CreateSutWithReadme(
            dialogReturns: null,
            readmeRefs: new[] { new IssueRef("octo/widgets", 1) });
        store.Seed(SessionId, new[] { "octo/widgets#42" });

        await vm.LoadAsync();
        vm.Links.Should().HaveCount(2);

        readme.Set(Array.Empty<IssueRef>());
        await vm.RefreshParsedRefsAsync();

        // Manual badge survives; parsed badge dropped.
        vm.Links.Should().ContainSingle();
        vm.Links[0].Ref.Number.Should().Be(42);
        vm.Links[0].Origin.Should().Be(IssueLinkOrigin.Manual);
    }

    [Fact]
    public async Task ReadmeChangedEvent_TriggersRefresh()
    {
        var (vm, _, _, readme) = CreateSutWithReadme(
            dialogReturns: null,
            readmeRefs: Array.Empty<IssueRef>());

        await vm.LoadAsync();
        vm.Links.Should().BeEmpty();

        readme.Set(new[] { new IssueRef("octo/widgets", 8) });
        readme.RaiseChanged(SessionId);

        // The provider raises synchronously; the VM's debounce schedules a
        // background refresh. Wait briefly for it to land.
        for (var i = 0; i < 60 && vm.Links.Count == 0; i++)
        {
            await Task.Delay(50);
        }

        vm.Links.Should().ContainSingle();
        vm.Links[0].Ref.Number.Should().Be(8);
        vm.Links[0].Origin.Should().Be(IssueLinkOrigin.ParsedFromReadme);
    }

    [Fact]
    public async Task ReadmeChangedEvent_OtherSession_IsIgnored()
    {
        var (vm, _, _, readme) = CreateSutWithReadme(
            dialogReturns: null,
            readmeRefs: Array.Empty<IssueRef>());

        await vm.LoadAsync();
        readme.Set(new[] { new IssueRef("octo/widgets", 8) });

        // Wrong session id — should not trigger a refresh.
        readme.RaiseChanged("ffffffffffffffffffffffffffffffffffffffff");
        await Task.Delay(150);

        vm.Links.Should().BeEmpty();
    }

    [Fact]
    public void Dispose_UnsubscribesFromReadmeChanged()
    {
        var (vm, _, _, readme) = CreateSutWithReadme(
            dialogReturns: null,
            readmeRefs: Array.Empty<IssueRef>());

        vm.Dispose();

        // Raising the event after dispose should not enlist the VM.
        readme.Set(new[] { new IssueRef("octo/widgets", 8) });
        readme.RaiseChanged(SessionId);

        // No deterministic effect to assert beyond no-throw and no exceptions.
        vm.Links.Should().BeEmpty();
    }

    [Fact]
    public async Task LoadAsync_ReadmeProviderThrows_DoesNotPropagate()
    {
        var (vm, _, _, readme) = CreateSutWithReadme(
            dialogReturns: null,
            readmeRefs: Array.Empty<IssueRef>());
        readme.ThrowOnGet = new InvalidOperationException("boom");

        var act = async () => await vm.LoadAsync();
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task LoadAsync_NoReadmeRefs_LeavesCollectionEmpty()
    {
        var (vm, _, _, _) = CreateSutWithReadme(
            dialogReturns: null,
            readmeRefs: Array.Empty<IssueRef>());

        await vm.LoadAsync();

        vm.Links.Should().BeEmpty();
    }

    private sealed class FakeReadmeRefsProvider : IReadmeIssueRefProvider
    {
        private IReadOnlyList<IssueRef> _refs = Array.Empty<IssueRef>();

        public Exception? ThrowOnGet { get; set; }

        public event EventHandler<ReadmeIssueRefsChangedEventArgs>? ReadmeChanged;

        public void Set(IEnumerable<IssueRef> refs) => _refs = refs.ToArray();

        public Task<IReadOnlyList<IssueRef>> GetParsedRefsAsync(string sessionId, string? defaultOwnerRepo, CancellationToken cancellationToken = default)
        {
            if (ThrowOnGet is not null)
            {
                throw ThrowOnGet;
            }
            return Task.FromResult(_refs);
        }

        public void RaiseChanged(string sessionId) =>
            ReadmeChanged?.Invoke(this, new ReadmeIssueRefsChangedEventArgs(sessionId));
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
