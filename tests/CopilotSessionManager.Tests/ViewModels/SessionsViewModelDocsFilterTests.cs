using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CopilotSessionManager.Core.Models;
using CopilotSessionManager.Core.Sessions;
using CopilotSessionManager.ViewModels;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CopilotSessionManager.Tests.ViewModels;

/// <summary>
/// V1.3 (#148): coverage for the Docs freshness filter dropdown chip group.
/// Reuses the V1.4 fakes so the dashboard wires the V1.3 canonical
/// constructor (which takes <see cref="IDocFreshnessService"/>).
/// </summary>
public class SessionsViewModelDocsFilterTests
{
    private static readonly DateTimeOffset Now = new(2026, 5, 8, 12, 0, 0, TimeSpan.Zero);

    private sealed class FakeDocFreshness : IDocFreshnessService
    {
        private readonly Dictionary<string, DocFreshnessState> _byId;

        public FakeDocFreshness(Dictionary<string, DocFreshnessState> byId) => _byId = byId;

        public DocFreshnessResult Evaluate(string sessionId, DateTimeOffset sessionCreatedAt)
        {
            var state = _byId.TryGetValue(sessionId, out var s) ? s : DocFreshnessState.NotApplicable;
            int? age = state is DocFreshnessState.Stale or DocFreshnessState.VeryStale ? 3 : null;
            return new DocFreshnessResult(state, age);
        }
    }

    private static Session Build(string id, SessionStatus status = SessionStatus.Idle) =>
        new(
            Id: id,
            Cwd: @"C:\ws\repo",
            Repository: "owner/repo",
            Branch: "main",
            Summary: $"Session {id}",
            HostType: "cli",
            CreatedAt: Now.AddMinutes(-30),
            UpdatedAt: Now.AddMinutes(-1),
            TurnCount: 1,
            Status: status,
            CopilotVersion: CopilotVersion.Zero,
            Locks: Array.Empty<SessionLockInfo>(),
            Producer: "agency");

    private static SessionsViewModel CreateSut(
        IEnumerable<Session> initial,
        IDocFreshnessService? docFreshness = null)
    {
        var tp = new SessionsViewModelTests.FixedTimeProvider(Now);
        var disc = new SessionsViewModelTests.FakeDiscoveryService(initial.ToArray());
        var labels = new SessionsViewModelTests.FakeLabelStore();
        var readme = new SessionsViewModelTests.FakeReadmeService();
        var launcher = new SessionsViewModelTests.FakeFileLauncher();

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
            starStore: null, docFreshness: docFreshness);
        vm.InitializeAsync().GetAwaiter().GetResult();
        return vm;
    }

    [Fact]
    public void DocsFilters_StartWithFourBucketsAllVisible()
    {
        var vm = CreateSut(Array.Empty<Session>());

        vm.DocsFilters.Should().HaveCount(4);
        vm.DocsFilters.Select(c => c.Bucket).Should().BeEquivalentTo(new[]
        {
            DocFreshnessFilterBucket.Fresh,
            DocFreshnessFilterBucket.Stale,
            DocFreshnessFilterBucket.Missing,
            DocFreshnessFilterBucket.NotApplicable,
        });
        vm.DocsFilters.All(c => c.IsVisible).Should().BeTrue();
        vm.DocsFilterSummary.Should().Be("Docs (all)");
    }

    [Fact]
    public void DocsFilterSummary_TransitionsAcrossAllPartialAndNoneStates()
    {
        var vm = CreateSut(Array.Empty<Session>());

        vm.DocsFilterSummary.Should().Be("Docs (all)");

        vm.DocsFilters.First(c => c.Bucket == DocFreshnessFilterBucket.Stale).IsVisible = false;
        vm.DocsFilterSummary.Should().Be("Docs (3 of 4)");

        foreach (var c in vm.DocsFilters)
        {
            c.IsVisible = false;
        }
        vm.DocsFilterSummary.Should().Be("Docs (none)");
    }

    [Fact]
    public void TogglingDocsChip_RefiresFilterSummaryAndRebuildsVisibleCollection()
    {
        var fresh = Build("fresh");
        var missing = Build("missing");
        var fakeFreshness = new FakeDocFreshness(new()
        {
            [fresh.Id] = DocFreshnessState.Fresh,
            [missing.Id] = DocFreshnessState.Missing,
        });
        var vm = CreateSut(new[] { fresh, missing }, fakeFreshness);

        vm.VisibleSessions.Should().HaveCount(2);

        var summaryRefires = 0;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SessionsViewModel.DocsFilterSummary))
                summaryRefires++;
        };

        // Hide the Missing bucket — only the Fresh card should remain visible.
        vm.DocsFilters.First(c => c.Bucket == DocFreshnessFilterBucket.Missing).IsVisible = false;

        summaryRefires.Should().BeGreaterThan(0);
        vm.DocsFilterSummary.Should().Be("Docs (3 of 4)");
        vm.VisibleSessions.Select(c => c.Id).Should().ContainSingle().Which.Should().Be(fresh.Id);
    }

    [Fact]
    public void HidingStaleChip_AlsoHidesVeryStaleSessions()
    {
        var stale = Build("stale");
        var veryStale = Build("very");
        var fakeFreshness = new FakeDocFreshness(new()
        {
            [stale.Id] = DocFreshnessState.Stale,
            [veryStale.Id] = DocFreshnessState.VeryStale,
        });
        var vm = CreateSut(new[] { stale, veryStale }, fakeFreshness);

        vm.VisibleSessions.Should().HaveCount(2);

        vm.DocsFilters.First(c => c.Bucket == DocFreshnessFilterBucket.Stale).IsVisible = false;

        vm.VisibleSessions.Should().BeEmpty(
            "Stale + VeryStale collapse to the single user-facing Stale chip (#148).");
    }

    [Fact]
    public void DocsFilterChip_BucketMappingFoldsVeryStaleIntoStale()
    {
        DocsFilterChip.ToBucket(DocFreshnessState.Fresh).Should().Be(DocFreshnessFilterBucket.Fresh);
        DocsFilterChip.ToBucket(DocFreshnessState.Stale).Should().Be(DocFreshnessFilterBucket.Stale);
        DocsFilterChip.ToBucket(DocFreshnessState.VeryStale).Should().Be(DocFreshnessFilterBucket.Stale);
        DocsFilterChip.ToBucket(DocFreshnessState.Missing).Should().Be(DocFreshnessFilterBucket.Missing);
        DocsFilterChip.ToBucket(DocFreshnessState.NotApplicable).Should().Be(DocFreshnessFilterBucket.NotApplicable);
    }
}
