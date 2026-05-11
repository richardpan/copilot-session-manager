using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CopilotSessionManager.Core.Models;
using CopilotSessionManager.Core.Sessions;
using CopilotSessionManager.Services;
using CopilotSessionManager.ViewModels;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CopilotSessionManager.Tests.ViewModels;

/// <summary>
/// Coverage for the V1.4 dashboard features added in #112 (star pin) and
/// #113 (producer filter chip group). Builds the dashboard via the V1.4
/// canonical constructor with an in-memory star store and pre-populated
/// Producer values on the seed sessions.
/// </summary>
public class SessionsViewModelV14Tests
{
    private static readonly DateTimeOffset Now = new(2026, 5, 8, 12, 0, 0, TimeSpan.Zero);

    private static Session Build(
        string id,
        SessionStatus status,
        DateTimeOffset? updatedAt = null,
        string? producer = "agency") =>
        new(
            Id: id,
            Cwd: @"C:\ws\repo",
            Repository: "owner/repo",
            Branch: "main",
            Summary: $"Session {id}",
            HostType: "cli",
            CreatedAt: Now.AddMinutes(-30),
            UpdatedAt: updatedAt ?? Now.AddMinutes(-1),
            TurnCount: 1,
            Status: status,
            CopilotVersion: CopilotVersion.Zero,
            Locks: Array.Empty<SessionLockInfo>(),
            Producer: producer);

    private static (SessionsViewModel vm, FakeStarStore stars,
        SessionsViewModelTests.FakeDiscoveryService disc) CreateSut(
        IEnumerable<Session> initial,
        IEnumerable<string>? preStarred = null)
    {
        var tp = new SessionsViewModelTests.FixedTimeProvider(Now);
        var disc = new SessionsViewModelTests.FakeDiscoveryService(initial.ToArray());
        var labels = new SessionsViewModelTests.FakeLabelStore();
        var readme = new SessionsViewModelTests.FakeReadmeService();
        var launcher = new SessionsViewModelTests.FakeFileLauncher();
        var stars = new FakeStarStore();
        if (preStarred is not null)
        {
            foreach (var id in preStarred)
            {
                stars.SetAsync(id).GetAwaiter().GetResult();
            }
        }

        var vm = new SessionsViewModel(
            disc, labels, readme, launcher,
            new SessionsViewModelTests.SyncDispatcher(), tp,
            modelCatalog: null, costCalculator: null,
            githubClient: null, checksClient: null,
            lockCleanup: null, sessionLauncher: null,
            loggerFactory: null, logger: NullLogger<SessionsViewModel>.Instance,
            issuesClient: null, linksStore: null,
            showAddIssueDialog: null, readmeIssueRefs: null,
            runningSessions: null, windowActivator: null,
            displayNameStore: null, deletionService: null, confirmDelete: null,
            starStore: stars);
        vm.InitializeAsync().GetAwaiter().GetResult();
        return (vm, stars, disc);
    }

    [Fact]
    public async Task StarredSession_SortsAboveUnstarred_RegardlessOfUpdatedAt()
    {
        // "old" was last updated long ago, "fresh" is the most recent.
        // Without star: fresh wins. With star on "old": old should pin to top.
        var fresh = Build("fresh", SessionStatus.Idle, updatedAt: Now);
        var old = Build("old", SessionStatus.Idle, updatedAt: Now.AddHours(-5));
        var (vm, _, _) = CreateSut(new[] { fresh, old }, preStarred: new[] { "old" });

        vm.Sessions[0].Id.Should().Be("old", because: "starred sessions pin to the top");
        vm.Sessions[1].Id.Should().Be("fresh");

        await vm.DisposeAsync();
    }

    [Fact]
    public async Task ToggleStar_Resorts_AndPersistsViaStore()
    {
        var fresh = Build("fresh", SessionStatus.Idle, updatedAt: Now);
        var old = Build("old", SessionStatus.Idle, updatedAt: Now.AddHours(-5));
        var (vm, stars, _) = CreateSut(new[] { fresh, old });

        vm.Sessions[0].Id.Should().Be("fresh");

        var oldCard = vm.Sessions.Single(c => c.Id == "old");
        await oldCard.ToggleStarCommand.ExecuteAsync(null);

        vm.Sessions[0].Id.Should().Be("old");
        (await stars.IsStarredAsync("old")).Should().BeTrue();

        await vm.DisposeAsync();
    }

    [Fact]
    public async Task StarsChanged_FromOutside_ReSortsLive()
    {
        var fresh = Build("fresh", SessionStatus.Idle, updatedAt: Now);
        var old = Build("old", SessionStatus.Idle, updatedAt: Now.AddHours(-5));
        var (vm, stars, _) = CreateSut(new[] { fresh, old });

        await stars.SetAsync("old"); // simulates another window starring it

        vm.Sessions[0].Id.Should().Be("old");
        vm.Sessions.Single(c => c.Id == "old").IsStarred.Should().BeTrue();

        await vm.DisposeAsync();
    }

    [Fact]
    public async Task ProducerFilters_ContainsChipPerDistinctProducer_PlusUnknown()
    {
        var (vm, _, _) = CreateSut(new[]
        {
            Build("a", SessionStatus.Idle, producer: "agency"),
            Build("b", SessionStatus.Idle, producer: "copilot-agent"),
            Build("c", SessionStatus.Idle, producer: null),
        });

        vm.ProducerFilters.Select(c => c.Label).Should().BeEquivalentTo(
            new[] { "agency", "copilot-agent", "(unknown)" });

        await vm.DisposeAsync();
    }

    [Fact]
    public async Task HidingProducer_DropsMatchingCardsFromVisible_AndAndsWithOtherFilters()
    {
        var (vm, _, _) = CreateSut(new[]
        {
            Build("a", SessionStatus.Idle, producer: "agency"),
            Build("b", SessionStatus.Idle, producer: "copilot-agent"),
        });

        vm.VisibleSessions.Should().HaveCount(2);

        var agencyChip = vm.ProducerFilters.Single(c => c.Label == "agency");
        agencyChip.IsVisible = false;

        vm.VisibleSessions.Select(c => c.Id).Should().BeEquivalentTo(new[] { "b" });

        // Toggle search on top to confirm AND with other predicates.
        vm.SearchText = "Session a";
        vm.VisibleSessions.Should().BeEmpty(
            because: "the only card matching search is hidden by the producer filter");

        agencyChip.IsVisible = true;
        vm.VisibleSessions.Select(c => c.Id).Should().BeEquivalentTo(new[] { "a" });

        await vm.DisposeAsync();
    }

    [Fact]
    public async Task UnknownProducerChip_Hides_NullProducerCards()
    {
        var (vm, _, _) = CreateSut(new[]
        {
            Build("a", SessionStatus.Idle, producer: "agency"),
            Build("b", SessionStatus.Idle, producer: null),
        });

        var unknownChip = vm.ProducerFilters.Single(c => c.Label == "(unknown)");
        unknownChip.IsVisible = false;

        vm.VisibleSessions.Select(c => c.Id).Should().BeEquivalentTo(new[] { "a" });

        await vm.DisposeAsync();
    }

    private sealed class FakeStarStore : ISessionStarStore
    {
        private readonly HashSet<string> _starred = new(StringComparer.OrdinalIgnoreCase);

        public event EventHandler<SessionStarChangedEventArgs>? StarsChanged;

        public Task<bool> IsStarredAsync(string sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult(_starred.Contains(sessionId));

        public Task<IReadOnlySet<string>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlySet<string>>(new HashSet<string>(_starred, StringComparer.OrdinalIgnoreCase));

        public Task SetAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            if (_starred.Add(sessionId))
            {
                StarsChanged?.Invoke(this, new SessionStarChangedEventArgs(sessionId, isStarred: true));
            }
            return Task.CompletedTask;
        }

        public Task RemoveAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            if (_starred.Remove(sessionId))
            {
                StarsChanged?.Invoke(this, new SessionStarChangedEventArgs(sessionId, isStarred: false));
            }
            return Task.CompletedTask;
        }
    }
}
