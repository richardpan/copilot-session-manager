using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CopilotSessionManager.Core.Models;
using CopilotSessionManager.Core.Sessions;
using CopilotSessionManager.ViewModels;
using FluentAssertions;
using Xunit;

namespace CopilotSessionManager.Tests.ViewModels;

/// <summary>
/// V1.3 (#146): the auto-refresh hooks on <see cref="SessionCardViewModel"/>.
/// These tests pin the three trigger paths (Open Terminal, Working →
/// AwaitingInput status transition, throttled snapshot tick) and verify
/// the throttle behaviour against a deterministic <see cref="TimeProvider"/>.
/// </summary>
public class SessionCardViewModelReadmeRefreshTests
{
    private static readonly DateTimeOffset Now = new(2026, 5, 8, 12, 0, 0, TimeSpan.Zero);

    private static Session BuildSession(
        string id = "sess-1",
        SessionStatus status = SessionStatus.Working,
        DateTimeOffset? updatedAt = null) =>
        new(
            Id: id,
            Cwd: @"C:\ws\repo",
            Repository: "owner/repo",
            Branch: "main",
            Summary: "test",
            HostType: "cli",
            CreatedAt: Now.AddMinutes(-30),
            UpdatedAt: updatedAt ?? Now,
            TurnCount: 1,
            Status: status,
            CopilotVersion: CopilotVersion.Zero,
            Locks: Array.Empty<SessionLockInfo>());

    private static SessionCardViewModel CreateCard(
        Session session,
        ISessionReadmeService? readmeService,
        TimeProvider timeProvider,
        ISessionLauncher? launcher = null)
        => new(
            session,
            SessionType.Exploratory,
            timeProvider,
            modelCatalog: null,
            costCalculator: null,
            fileLauncher: null,
            lockCleanup: null,
            sessionLauncher: launcher,
            logger: null,
            openMergeWizard: null,
            issueLinks: null,
            runningSessions: null,
            windowActivator: null,
            displayNameStore: null,
            displayNameOverride: null,
            deletionService: null,
            confirmDelete: null,
            starStore: null,
            isStarred: false,
            onDeleted: null,
            docFreshness: null,
            readmeService: readmeService);

    [Fact]
    public async Task UpdateFrom_WorkingToAwaitingInput_ForcesReadmeRefresh()
    {
        var readme = new RecordingReadmeService();
        var clock = new MutableTimeProvider(Now);
        var sut = CreateCard(BuildSession(status: SessionStatus.Working), readme, clock);

        // First UpdateFrom is the throttled snapshot tick path — fires once
        // because lastRefresh is MinValue. Wait for the queued task so
        // subsequent assertions are deterministic.
        sut.UpdateFrom(BuildSession(status: SessionStatus.Working));
        await readme.WaitForCallsAsync(1);
        readme.Calls.Should().Be(1);

        // Within the throttle, a same-status tick should NOT trigger again.
        clock.Advance(TimeSpan.FromMinutes(1));
        sut.UpdateFrom(BuildSession(status: SessionStatus.Working));
        await Task.Delay(50); // give any rogue task a chance to run
        readme.Calls.Should().Be(1);

        // But Working → AwaitingInput bypasses the throttle.
        sut.UpdateFrom(BuildSession(status: SessionStatus.AwaitingInput));
        await readme.WaitForCallsAsync(2);
        readme.Calls.Should().Be(2);
    }

    [Fact]
    public async Task UpdateFrom_RespectsThrottleWindow()
    {
        var readme = new RecordingReadmeService();
        var clock = new MutableTimeProvider(Now);
        var sut = CreateCard(BuildSession(), readme, clock);

        sut.UpdateFrom(BuildSession());
        await readme.WaitForCallsAsync(1);

        // Just under the throttle window — should be skipped.
        clock.Advance(SessionCardViewModel.AutoRefreshThrottle - TimeSpan.FromSeconds(1));
        sut.UpdateFrom(BuildSession());
        await Task.Delay(50);
        readme.Calls.Should().Be(1);

        // Past the throttle window — should fire again.
        clock.Advance(TimeSpan.FromSeconds(2));
        sut.UpdateFrom(BuildSession());
        await readme.WaitForCallsAsync(2);
        readme.Calls.Should().Be(2);
    }

    [Fact]
    public async Task UpdateFrom_WithoutReadmeService_NoOps()
    {
        var clock = new MutableTimeProvider(Now);
        var sut = CreateCard(BuildSession(), readmeService: null, timeProvider: clock);

        // Should not throw and should not crash on a null _readmeService.
        sut.UpdateFrom(BuildSession(status: SessionStatus.AwaitingInput));
        await Task.Delay(50);
    }

    [Fact]
    public async Task OpenAsync_AfterFreshLaunch_ForcesReadmeRefresh()
    {
        var readme = new RecordingReadmeService();
        var clock = new MutableTimeProvider(Now);
        var launcher = new RecordingLauncher();
        var sut = CreateCard(BuildSession(), readme, clock, launcher);

        // Trip the throttle so we know OpenAsync is bypassing it.
        sut.UpdateFrom(BuildSession());
        await readme.WaitForCallsAsync(1);

        await sut.OpenCommand.ExecuteAsync(null);

        await readme.WaitForCallsAsync(2);
        readme.Calls.Should().Be(2);
        launcher.Calls.Should().HaveCount(1);
    }

    [Fact]
    public async Task OpenAsync_LaunchFailure_DoesNotTriggerRefresh()
    {
        var readme = new RecordingReadmeService();
        var clock = new MutableTimeProvider(Now);
        var sut = CreateCard(BuildSession(), readme, clock, new ThrowingLauncher());

        await sut.OpenCommand.ExecuteAsync(null);

        await Task.Delay(50);
        readme.Calls.Should().Be(0);
    }

    private sealed class RecordingReadmeService : ISessionReadmeService
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);

        public Task<string> EnsureAsync(Session session, SessionType label, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(string.Empty);
        }

        public Task AppendAsync(string sessionId, string markdown, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public string GetReadmePath(string sessionId) => string.Empty;

        public async Task WaitForCallsAsync(int expected, int timeoutMs = 2000)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (Calls < expected && sw.ElapsedMilliseconds < timeoutMs)
            {
                await Task.Delay(10);
            }
        }
    }

    private sealed class RecordingLauncher : ISessionLauncher
    {
        public List<(string sessionId, string? cwd)> Calls { get; } = new();
        public Task<SessionLaunchResult> LaunchAsync(string sessionId, string? workingDirectory = null, CancellationToken cancellationToken = default)
        {
            Calls.Add((sessionId, workingDirectory));
            return Task.FromResult(new SessionLaunchResult(99, "pwsh.exe", "copilot --resume " + sessionId, workingDirectory ?? ""));
        }
        public Task<SessionLaunchResult> LaunchNewAsync(string? workingDirectory = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new SessionLaunchResult(100, "pwsh.exe", "copilot", workingDirectory ?? ""));
    }

    private sealed class ThrowingLauncher : ISessionLauncher
    {
        public Task<SessionLaunchResult> LaunchAsync(string sessionId, string? workingDirectory = null, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("boom");
        public Task<SessionLaunchResult> LaunchNewAsync(string? workingDirectory = null, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("boom");
    }

    private sealed class MutableTimeProvider : TimeProvider
    {
        private DateTimeOffset _now;
        public MutableTimeProvider(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
    }
}
