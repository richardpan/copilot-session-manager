using System;
using System.Linq;
using CopilotSessionManager.Core.Models;
using CopilotSessionManager.Services;
using CopilotSessionManager.ViewModels;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CopilotSessionManager.Tests.ViewModels;

/// <summary>
/// Tests for the V1.3 (#110) name-search filter on
/// <see cref="SessionsViewModel"/>. Reuses the public fakes from
/// <see cref="SessionsViewModelTests"/> to keep the seam identical to
/// production wiring.
/// </summary>
public class SessionsViewModelSearchTests
{
    private static readonly DateTimeOffset Now = new(2026, 5, 8, 12, 0, 0, TimeSpan.Zero);

    private static Session BuildWithSummary(string id, string summary) =>
        new(
            Id: id,
            Cwd: @"C:\ws\repo",
            Repository: "owner/repo",
            Branch: "main",
            Summary: summary,
            HostType: "cli",
            CreatedAt: Now.AddMinutes(-30),
            UpdatedAt: Now.AddMinutes(-1),
            TurnCount: 1,
            Status: SessionStatus.Idle,
            CopilotVersion: CopilotVersion.Zero,
            Locks: Array.Empty<SessionLockInfo>());

    private static SessionsViewModel CreateSut(params Session[] sessions)
    {
        var tp = new SessionsViewModelTests.FixedTimeProvider(Now);
        var disc = new SessionsViewModelTests.FakeDiscoveryService(sessions);
        var labels = new SessionsViewModelTests.FakeLabelStore();
        var readme = new SessionsViewModelTests.FakeReadmeService();
        var launcher = new SessionsViewModelTests.FakeFileLauncher();
        var vm = new SessionsViewModel(
            disc, labels, readme, launcher,
            new SessionsViewModelTests.SyncDispatcher(), tp,
            NullLogger<SessionsViewModel>.Instance);
        vm.InitializeAsync().GetAwaiter().GetResult();
        return vm;
    }

    [Fact]
    public void SearchText_DefaultsToEmpty_AndShowsAllSessions()
    {
        var vm = CreateSut(
            BuildWithSummary("a-id-1111", "Refactor login flow"),
            BuildWithSummary("b-id-2222", "Fix billing bug"),
            BuildWithSummary("c-id-3333", "Add search bar"));

        vm.SearchText.Should().BeEmpty();
        vm.VisibleSessions.Should().HaveCount(3);
    }

    [Fact]
    public void SearchText_Whitespace_ShowsAllSessions()
    {
        var vm = CreateSut(
            BuildWithSummary("a-id-1111", "Refactor login flow"),
            BuildWithSummary("b-id-2222", "Fix billing bug"));

        vm.SearchText = "   \t  ";

        vm.VisibleSessions.Should().HaveCount(2);
    }

    [Fact]
    public void SearchText_SingleToken_FiltersByContains_CaseInsensitive()
    {
        var vm = CreateSut(
            BuildWithSummary("a-id-1111", "Refactor LOGIN flow"),
            BuildWithSummary("b-id-2222", "Fix billing bug"),
            BuildWithSummary("c-id-3333", "Update docs"));

        vm.SearchText = "login";

        vm.VisibleSessions.Should().HaveCount(1);
        vm.VisibleSessions[0].Id.Should().Be("a-id-1111");
    }

    [Fact]
    public void SearchText_MultipleTokens_AreAndedTogether()
    {
        var vm = CreateSut(
            BuildWithSummary("a-id-1111", "Refactor login flow"),
            BuildWithSummary("b-id-2222", "Refactor billing flow"),
            BuildWithSummary("c-id-3333", "Add login screen"));

        // Both "refactor" AND "login" must hit; only "a" qualifies.
        vm.SearchText = "refactor login";

        vm.VisibleSessions.Should().HaveCount(1);
        vm.VisibleSessions[0].Id.Should().Be("a-id-1111");
    }

    [Fact]
    public void SearchText_TokensAreOrderIndependent()
    {
        var vm = CreateSut(
            BuildWithSummary("a-id-1111", "Refactor login flow"));

        vm.SearchText = "login refactor";

        vm.VisibleSessions.Should().ContainSingle();
    }

    [Fact]
    public void SearchText_NoMatches_YieldsEmptyVisibleList()
    {
        var vm = CreateSut(
            BuildWithSummary("a-id-1111", "Refactor login flow"),
            BuildWithSummary("b-id-2222", "Fix billing bug"));

        vm.SearchText = "qwertyuiop";

        vm.VisibleSessions.Should().BeEmpty();
        vm.Sessions.Should().HaveCount(2,
            "the underlying collection is unchanged — only VisibleSessions filters");
    }

    [Fact]
    public void SearchText_Cleared_RestoresFullList()
    {
        var vm = CreateSut(
            BuildWithSummary("a-id-1111", "Refactor login flow"),
            BuildWithSummary("b-id-2222", "Fix billing bug"));

        vm.SearchText = "billing";
        vm.VisibleSessions.Should().HaveCount(1);

        vm.SearchText = "";
        vm.VisibleSessions.Should().HaveCount(2);
    }

    [Fact]
    public void SearchText_ComposesWithShowInactiveFilter()
    {
        // Two sessions match the search; one is inactive.
        // With ShowInactive=false the inactive one drops out.
        var vm = CreateSut(
            new Session(
                Id: "a-id-1111",
                Cwd: @"C:\ws\repo",
                Repository: "owner/repo",
                Branch: "main",
                Summary: "search needle alpha",
                HostType: "cli",
                CreatedAt: Now.AddMinutes(-30),
                UpdatedAt: Now.AddMinutes(-1),
                TurnCount: 1,
                Status: SessionStatus.Idle,
                CopilotVersion: CopilotVersion.Zero,
                Locks: Array.Empty<SessionLockInfo>()),
            new Session(
                Id: "b-id-2222",
                Cwd: @"C:\ws\repo",
                Repository: "owner/repo",
                Branch: "main",
                Summary: "search needle beta",
                HostType: "cli",
                CreatedAt: Now.AddMinutes(-30),
                UpdatedAt: Now.AddMinutes(-1),
                TurnCount: 1,
                Status: SessionStatus.Inactive,
                CopilotVersion: CopilotVersion.Zero,
                Locks: Array.Empty<SessionLockInfo>()));

        vm.SearchText = "needle";
        vm.VisibleSessions.Should().HaveCount(2);

        vm.ShowInactive = false;

        vm.VisibleSessions.Should().HaveCount(1);
        vm.VisibleSessions[0].Id.Should().Be("a-id-1111");
    }
}
