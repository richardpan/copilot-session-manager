using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CopilotSessionManager.Core.Models;
using CopilotSessionManager.Core.Sessions;
using CopilotSessionManager.ViewModels;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CopilotSessionManager.Tests.ViewModels;

public class CrashBannerViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 5, 8, 12, 0, 0, TimeSpan.Zero);

    private static Session Build(string id, SessionStatus status) =>
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
            Locks: Array.Empty<SessionLockInfo>());

    private static (SessionsViewModel Vm, SessionsViewModelTests.FakeDiscoveryService Discovery, RecordingCleanup? Cleanup) CreateSut(
        IReadOnlyList<Session> initial,
        RecordingCleanup? cleanup = null)
    {
        var tp = new SessionsViewModelTests.FixedTimeProvider(Now);
        var disc = new SessionsViewModelTests.FakeDiscoveryService(initial);
        var vm = new SessionsViewModel(
            disc,
            new SessionsViewModelTests.FakeLabelStore(),
            new SessionsViewModelTests.FakeReadmeService(),
            new SessionsViewModelTests.FakeFileLauncher(),
            new SessionsViewModelTests.SyncDispatcher(),
            tp,
            modelCatalog: null,
            costCalculator: null,
            githubClient: null,
            lockCleanup: cleanup,
            sessionLauncher: null,
            loggerFactory: null,
            NullLogger<SessionsViewModel>.Instance);
        vm.InitializeAsync().GetAwaiter().GetResult();
        return (vm, disc, cleanup);
    }

    [Fact]
    public async Task InitialState_WithNoCrashedSessions_IsHidden()
    {
        var (vm, _, _) = CreateSut(new[] { Build("a", SessionStatus.Idle) });

        vm.CrashBanner.IsVisible.Should().BeFalse();
        vm.CrashBanner.CrashedCount.Should().Be(0);
        vm.CrashBanner.Message.Should().Be("0 sessions crashed since last scan.");

        await vm.DisposeAsync();
    }

    [Fact]
    public async Task OneCrashedSession_UsesSingularCopy()
    {
        var (vm, _, _) = CreateSut(new[] { Build("a", SessionStatus.Orphaned) });

        vm.CrashBanner.IsVisible.Should().BeTrue();
        vm.CrashBanner.CrashedCount.Should().Be(1);
        vm.CrashBanner.Message.Should().Be("1 session crashed since last scan.");

        await vm.DisposeAsync();
    }

    [Fact]
    public async Task ThreeCrashedSessions_UsesPluralCopy()
    {
        var (vm, _, _) = CreateSut(new[]
        {
            Build("a", SessionStatus.Orphaned),
            Build("b", SessionStatus.Orphaned),
            Build("c", SessionStatus.Orphaned),
        });

        vm.CrashBanner.IsVisible.Should().BeTrue();
        vm.CrashBanner.CrashedCount.Should().Be(3);
        vm.CrashBanner.Message.Should().Be("3 sessions crashed since last scan.");

        await vm.DisposeAsync();
    }

    [Fact]
    public async Task Dismiss_HidesCurrentlyCrashedIds()
    {
        var (vm, _, _) = CreateSut(new[]
        {
            Build("a", SessionStatus.Orphaned),
            Build("b", SessionStatus.Orphaned),
        });

        vm.CrashBanner.DismissCommand.Execute(null);

        vm.CrashBanner.IsVisible.Should().BeFalse();
        vm.CrashBanner.CrashedCount.Should().Be(2);
        vm.CrashBanner.Message.Should().Be("2 sessions crashed since last scan.");

        await vm.DisposeAsync();
    }

    [Fact]
    public async Task NewCrashedId_AfterDismiss_Reappears()
    {
        var (vm, disc, _) = CreateSut(new[] { Build("a", SessionStatus.Orphaned) });
        vm.CrashBanner.DismissCommand.Execute(null);

        disc.RaiseChanged(new[]
        {
            Build("a", SessionStatus.Orphaned),
            Build("b", SessionStatus.Orphaned),
        });

        vm.CrashBanner.IsVisible.Should().BeTrue();
        vm.CrashBanner.CrashedCount.Should().Be(2);
        vm.CrashBanner.Message.Should().Be("2 sessions crashed since last scan.");

        await vm.DisposeAsync();
    }

    [Fact]
    public async Task DismissedIds_ReorphanedAfterCleanup_DoNotRetrigger()
    {
        var (vm, disc, _) = CreateSut(new[]
        {
            Build("a", SessionStatus.Orphaned),
            Build("b", SessionStatus.Orphaned),
            Build("c", SessionStatus.Orphaned),
        });
        vm.CrashBanner.DismissCommand.Execute(null);

        disc.RaiseChanged(new[]
        {
            Build("a", SessionStatus.Inactive),
            Build("b", SessionStatus.Inactive),
            Build("c", SessionStatus.Inactive),
        });
        disc.RaiseChanged(new[]
        {
            Build("a", SessionStatus.Orphaned),
            Build("b", SessionStatus.Orphaned),
            Build("c", SessionStatus.Orphaned),
        });

        vm.CrashBanner.IsVisible.Should().BeFalse("dismissed ids remain dismissed for this app session");
        vm.CrashBanner.CrashedCount.Should().Be(3);

        await vm.DisposeAsync();
    }

    [Fact]
    public async Task CleanUpAllCommand_DelegatesToSessionsCommand_ThenRefreshes()
    {
        var cleanup = new RecordingCleanup();
        var (vm, disc, _) = CreateSut(new[] { Build("a", SessionStatus.Orphaned) }, cleanup);
        cleanup.OnCleanupAll = () =>
        {
            disc.RaiseChanged(new[] { Build("a", SessionStatus.Inactive) });
            return Task.CompletedTask;
        };

        await vm.CrashBanner.CleanUpAllCommand.ExecuteAsync(null);

        cleanup.BulkCalls.Should().Be(1);
        vm.CrashBanner.IsVisible.Should().BeFalse();
        vm.CrashBanner.CrashedCount.Should().Be(0);
        vm.CrashBanner.Message.Should().Be("0 sessions crashed since last scan.");

        await vm.DisposeAsync();
    }

    [Fact]
    public async Task RemovedCrashedSession_RefreshesCount()
    {
        var (vm, disc, _) = CreateSut(new[] { Build("a", SessionStatus.Orphaned) });

        disc.RaiseChanged(Array.Empty<Session>());

        vm.CrashBanner.IsVisible.Should().BeFalse();
        vm.CrashBanner.CrashedCount.Should().Be(0);
        vm.CrashBanner.Message.Should().Be("0 sessions crashed since last scan.");

        await vm.DisposeAsync();
    }

    private sealed class RecordingCleanup : ISessionLockCleanup
    {
        public int BulkCalls { get; private set; }
        public Func<Task>? OnCleanupAll { get; set; }

        public Task<int> CleanupAsync(string sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

        public async Task<SessionLockCleanupResult> CleanupAllAsync(CancellationToken cancellationToken = default)
        {
            BulkCalls++;
            if (OnCleanupAll is not null)
            {
                await OnCleanupAll().ConfigureAwait(false);
            }
            return SessionLockCleanupResult.Empty;
        }
    }
}
