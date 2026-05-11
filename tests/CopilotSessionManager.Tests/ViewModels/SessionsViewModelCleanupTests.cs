using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CopilotSessionManager.Core.Models;
using CopilotSessionManager.Core.Sessions;
using CopilotSessionManager.ViewModels;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CopilotSessionManager.Tests.ViewModels;

public class SessionsViewModelCleanupTests
{
    private static readonly DateTimeOffset Now = new(2026, 5, 8, 12, 0, 0, TimeSpan.Zero);

    private static Session BuildSession(string id, SessionStatus status = SessionStatus.Orphaned) =>
        new(
            Id: id,
            Cwd: @"C:\ws\repo",
            Repository: "owner/repo",
            Branch: "main",
            Summary: "test",
            HostType: "cli",
            CreatedAt: Now.AddMinutes(-30),
            UpdatedAt: Now.AddMinutes(-2),
            TurnCount: 1,
            Status: status,
            CopilotVersion: CopilotVersion.Zero,
            Locks: Array.Empty<SessionLockInfo>());

    private static SessionsViewModel CreateSut(
        IEnumerable<Session> initial,
        FakeBulkCleanup? cleanup = null)
    {
        var tp = new SessionsViewModelTests.FixedTimeProvider(Now);
        var disc = new SessionsViewModelTests.FakeDiscoveryService(initial.ToArray());
        var labels = new SessionsViewModelTests.FakeLabelStore();
        var readme = new SessionsViewModelTests.FakeReadmeService();
        var launcher = new SessionsViewModelTests.FakeFileLauncher();
        var vm = new SessionsViewModel(
            disc, labels, readme, launcher, new SessionsViewModelTests.SyncDispatcher(), tp,
            modelCatalog: null, costCalculator: null, githubClient: null,
            lockCleanup: cleanup, sessionLauncher: null, loggerFactory: null,
            NullLogger<SessionsViewModel>.Instance);
        vm.InitializeAsync().GetAwaiter().GetResult();
        return vm;
    }

    [Fact]
    public void CanExecute_FalseWhenCleanupNotInjected()
    {
        var vm = CreateSut(new[] { BuildSession("s1") });
        vm.CleanAllStaleLocksCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public void CanExecute_TrueWhenCleanupInjected()
    {
        var vm = CreateSut(new[] { BuildSession("s1") }, new FakeBulkCleanup());
        vm.CleanAllStaleLocksCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public async Task CleanAllStaleLocksAsync_RemovesNothing_PostsZeroMessage()
    {
        var cleanup = new FakeBulkCleanup(new SessionLockCleanupResult(0, 0));
        var vm = CreateSut(new[] { BuildSession("s1") }, cleanup);

        await vm.CleanAllStaleLocksAsync();

        cleanup.BulkCalls.Should().Be(1);
        vm.StatusMessage.Should().Be("No stale lock files found.");
    }

    [Fact]
    public async Task CleanAllStaleLocksAsync_PostsResultMessage()
    {
        var cleanup = new FakeBulkCleanup(new SessionLockCleanupResult(LocksRemoved: 5, SessionsAffected: 2));
        var vm = CreateSut(new[] { BuildSession("s1") }, cleanup);

        await vm.CleanAllStaleLocksAsync();

        vm.StatusMessage.Should().Be("Removed 5 stale lock(s) across 2 session(s).");
    }

    [Fact]
    public async Task CleanAllStaleLocksAsync_PostsErrorMessage_OnFailure()
    {
        var cleanup = new FakeBulkCleanup(throwMessage: "io broke");
        var vm = CreateSut(new[] { BuildSession("s1") }, cleanup);

        await vm.CleanAllStaleLocksAsync();
        vm.StatusMessage.Should().Contain("io broke");
    }

    [Fact]
    public async Task InitializeAsync_AutoCleanFalse_DoesNotInvokeCleanup()
    {
        // Bypass the cached CreateSut helper because it always calls
        // InitializeAsync() with the default (false) — we want to assert that
        // path also doesn't trigger any cleanup.
        var cleanup = new FakeBulkCleanup();
        var tp = new SessionsViewModelTests.FixedTimeProvider(Now);
        var disc = new SessionsViewModelTests.FakeDiscoveryService(new[] { BuildSession("s1") });
        var labels = new SessionsViewModelTests.FakeLabelStore();
        var readme = new SessionsViewModelTests.FakeReadmeService();
        var launcher = new SessionsViewModelTests.FakeFileLauncher();
        var vm = new SessionsViewModel(
            disc, labels, readme, launcher, new SessionsViewModelTests.SyncDispatcher(), tp,
            modelCatalog: null, costCalculator: null, githubClient: null,
            lockCleanup: cleanup, sessionLauncher: null, loggerFactory: null,
            NullLogger<SessionsViewModel>.Instance);

        await vm.InitializeAsync(autoCleanStaleLocksOnStartup: false);

        cleanup.BulkCalls.Should().Be(0,
            "with the V1.8 (#74) opt-in disabled, startup must never sweep locks");
    }

    [Fact]
    public async Task InitializeAsync_AutoCleanTrue_InvokesCleanupExactlyOnce_AfterScan()
    {
        var cleanup = new FakeBulkCleanup(new SessionLockCleanupResult(LocksRemoved: 3, SessionsAffected: 2));
        var tp = new SessionsViewModelTests.FixedTimeProvider(Now);
        var disc = new SessionsViewModelTests.FakeDiscoveryService(new[] { BuildSession("s1") });
        var labels = new SessionsViewModelTests.FakeLabelStore();
        var readme = new SessionsViewModelTests.FakeReadmeService();
        var launcher = new SessionsViewModelTests.FakeFileLauncher();
        var vm = new SessionsViewModel(
            disc, labels, readme, launcher, new SessionsViewModelTests.SyncDispatcher(), tp,
            modelCatalog: null, costCalculator: null, githubClient: null,
            lockCleanup: cleanup, sessionLauncher: null, loggerFactory: null,
            NullLogger<SessionsViewModel>.Instance);

        await vm.InitializeAsync(autoCleanStaleLocksOnStartup: true);

        cleanup.BulkCalls.Should().Be(1,
            "the V1.8 (#74) opt-in must trigger CleanupAllAsync exactly once after the initial scan");
        vm.StatusMessage.Should().Be("Removed 3 stale lock(s) across 2 session(s).",
            "the post-cleanup status message must reflect what was removed");
    }

    [Fact]
    public async Task InitializeAsync_AutoCleanTrue_NoLockCleanupInjected_DoesNotThrow()
    {
        // Some hosts (tests, headless tools) construct SessionsViewModel
        // without an ISessionLockCleanup. Auto-clean must degrade gracefully
        // rather than crash the dashboard during startup.
        var tp = new SessionsViewModelTests.FixedTimeProvider(Now);
        var disc = new SessionsViewModelTests.FakeDiscoveryService(new[] { BuildSession("s1") });
        var labels = new SessionsViewModelTests.FakeLabelStore();
        var readme = new SessionsViewModelTests.FakeReadmeService();
        var launcher = new SessionsViewModelTests.FakeFileLauncher();
        var vm = new SessionsViewModel(
            disc, labels, readme, launcher, new SessionsViewModelTests.SyncDispatcher(), tp,
            modelCatalog: null, costCalculator: null, githubClient: null,
            lockCleanup: null, sessionLauncher: null, loggerFactory: null,
            NullLogger<SessionsViewModel>.Instance);

        var act = () => vm.InitializeAsync(autoCleanStaleLocksOnStartup: true);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task InitializeAsync_SecondCallIsIdempotent_NoSecondAutoClean()
    {
        var cleanup = new FakeBulkCleanup();
        var tp = new SessionsViewModelTests.FixedTimeProvider(Now);
        var disc = new SessionsViewModelTests.FakeDiscoveryService(new[] { BuildSession("s1") });
        var labels = new SessionsViewModelTests.FakeLabelStore();
        var readme = new SessionsViewModelTests.FakeReadmeService();
        var launcher = new SessionsViewModelTests.FakeFileLauncher();
        var vm = new SessionsViewModel(
            disc, labels, readme, launcher, new SessionsViewModelTests.SyncDispatcher(), tp,
            modelCatalog: null, costCalculator: null, githubClient: null,
            lockCleanup: cleanup, sessionLauncher: null, loggerFactory: null,
            NullLogger<SessionsViewModel>.Instance);

        await vm.InitializeAsync(autoCleanStaleLocksOnStartup: true);
        await vm.InitializeAsync(autoCleanStaleLocksOnStartup: true);

        cleanup.BulkCalls.Should().Be(1,
            "InitializeAsync is idempotent (already-started short-circuits), so auto-clean must run at most once per process");
    }

    private sealed class FakeBulkCleanup : ISessionLockCleanup
    {
        private readonly SessionLockCleanupResult _bulk;
        private readonly string? _throwMessage;
        public int BulkCalls { get; private set; }

        public FakeBulkCleanup() : this(SessionLockCleanupResult.Empty) { }
        public FakeBulkCleanup(SessionLockCleanupResult bulk) { _bulk = bulk; }
        public FakeBulkCleanup(string throwMessage) { _bulk = SessionLockCleanupResult.Empty; _throwMessage = throwMessage; }

        public Task<int> CleanupAsync(string sessionId, CancellationToken cancellationToken = default)
            => Task.FromResult(0);
        public Task<SessionLockCleanupResult> CleanupAllAsync(CancellationToken cancellationToken = default)
        {
            BulkCalls++;
            if (_throwMessage is not null)
                throw new IOException(_throwMessage);
            return Task.FromResult(_bulk);
        }
    }
}
