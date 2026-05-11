using System;
using System.Threading;
using System.Threading.Tasks;
using CopilotSessionManager.Core.Models;
using CopilotSessionManager.Core.Sessions;
using CopilotSessionManager.ViewModels;
using FluentAssertions;
using Xunit;

namespace CopilotSessionManager.Tests.ViewModels;

public class SessionCardViewModelCleanupTests
{
    private static readonly DateTimeOffset Now = new(2026, 5, 8, 12, 0, 0, TimeSpan.Zero);

    private static Session BuildSession(SessionStatus status = SessionStatus.Orphaned, string id = "sess-1", string? cwd = @"C:\ws\repo") =>
        new(
            Id: id,
            Cwd: cwd,
            Repository: "owner/repo",
            Branch: "main",
            Summary: "test",
            HostType: "cli",
            CreatedAt: Now.AddMinutes(-10),
            UpdatedAt: Now,
            TurnCount: 1,
            Status: status,
            CopilotVersion: CopilotVersion.Zero,
            Locks: Array.Empty<SessionLockInfo>());

    private static SessionCardViewModel CreateCard(
        Session session,
        ISessionLockCleanup? cleanup = null,
        ISessionLauncher? launcher = null) =>
        new(session, SessionType.Exploratory, new FixedTimeProvider(Now),
            modelCatalog: null, costCalculator: null, fileLauncher: null,
            lockCleanup: cleanup, sessionLauncher: launcher, logger: null);

    [Fact]
    public void IsCrashed_True_OnlyForOrphaned()
    {
        CreateCard(BuildSession(SessionStatus.Orphaned)).IsCrashed.Should().BeTrue();
        CreateCard(BuildSession(SessionStatus.Inactive)).IsCrashed.Should().BeFalse();
        CreateCard(BuildSession(SessionStatus.Working)).IsCrashed.Should().BeFalse();
    }

    [Fact]
    public void StatusLabel_ReturnsCrashed_ForOrphaned()
    {
        CreateCard(BuildSession(SessionStatus.Orphaned)).StatusLabel.Should().Be("Crashed");
    }

    [Fact]
    public void Commands_DisabledWhenNotCrashed()
    {
        var sut = CreateCard(BuildSession(SessionStatus.Inactive),
            cleanup: new RecordingCleanup(),
            launcher: new RecordingLauncher());
        sut.CleanupStaleLocksCommand.CanExecute(null).Should().BeFalse();
        sut.ResumeCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public void Commands_DisabledWhenServicesMissing()
    {
        var sut = CreateCard(BuildSession(SessionStatus.Orphaned));
        sut.CleanupStaleLocksCommand.CanExecute(null).Should().BeFalse();
        sut.ResumeCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public async Task CleanupCommand_ReportsCount()
    {
        var cleanup = new RecordingCleanup(perSessionResult: 3);
        var sut = CreateCard(BuildSession(SessionStatus.Orphaned), cleanup: cleanup);

        await sut.CleanupStaleLocksCommand.ExecuteAsync(null);

        cleanup.PerSessionCalls.Should().ContainSingle().Which.Should().Be("sess-1");
        sut.LastActionMessage.Should().Be("Removed 3 stale locks.");
    }

    [Fact]
    public async Task CleanupCommand_ReportsZeroCount()
    {
        var cleanup = new RecordingCleanup(perSessionResult: 0);
        var sut = CreateCard(BuildSession(SessionStatus.Orphaned), cleanup: cleanup);
        await sut.CleanupStaleLocksCommand.ExecuteAsync(null);
        sut.LastActionMessage.Should().Be("No stale locks to remove.");
    }

    [Fact]
    public async Task CleanupCommand_ReportsSingleCount()
    {
        var cleanup = new RecordingCleanup(perSessionResult: 1);
        var sut = CreateCard(BuildSession(SessionStatus.Orphaned), cleanup: cleanup);
        await sut.CleanupStaleLocksCommand.ExecuteAsync(null);
        sut.LastActionMessage.Should().Be("Removed 1 stale lock.");
    }

    [Fact]
    public async Task ResumeCommand_CallsCleanupThenLauncher()
    {
        var cleanup = new RecordingCleanup(perSessionResult: 1);
        var launcher = new RecordingLauncher();
        var sut = CreateCard(BuildSession(SessionStatus.Orphaned, cwd: @"C:\ws\repo"),
            cleanup: cleanup, launcher: launcher);

        await sut.ResumeCommand.ExecuteAsync(null);

        cleanup.PerSessionCalls.Should().ContainSingle();
        launcher.Calls.Should().ContainSingle();
        launcher.Calls[0].sessionId.Should().Be("sess-1");
        launcher.Calls[0].cwd.Should().Be(@"C:\ws\repo");
        sut.LastActionMessage.Should().Contain("Launched PowerShell");
    }

    [Fact]
    public async Task ResumeCommand_LaunchesEvenWhenCleanupFails()
    {
        var cleanup = new RecordingCleanup(throwOnPerSession: true);
        var launcher = new RecordingLauncher();
        var sut = CreateCard(BuildSession(SessionStatus.Orphaned),
            cleanup: cleanup, launcher: launcher);

        await sut.ResumeCommand.ExecuteAsync(null);
        launcher.Calls.Should().ContainSingle();
    }

    [Fact]
    public async Task ResumeCommand_RecordsFailureMessage_WhenLauncherThrows()
    {
        var sut = CreateCard(BuildSession(SessionStatus.Orphaned),
            cleanup: new RecordingCleanup(),
            launcher: new ThrowingLauncher("boom"));
        await sut.ResumeCommand.ExecuteAsync(null);
        sut.LastActionMessage.Should().Contain("boom");
    }

    [Fact]
    public void UpdateFrom_ReevaluatesIsCrashedAndCommandState()
    {
        var sut = CreateCard(BuildSession(SessionStatus.Inactive),
            cleanup: new RecordingCleanup(),
            launcher: new RecordingLauncher());
        sut.IsCrashed.Should().BeFalse();
        sut.CleanupStaleLocksCommand.CanExecute(null).Should().BeFalse();

        sut.UpdateFrom(BuildSession(SessionStatus.Orphaned));

        sut.IsCrashed.Should().BeTrue();
        sut.CleanupStaleLocksCommand.CanExecute(null).Should().BeTrue();
        sut.ResumeCommand.CanExecute(null).Should().BeTrue();
    }

    private sealed class RecordingCleanup : ISessionLockCleanup
    {
        private readonly int _perSessionResult;
        private readonly bool _throwOnPerSession;
        public List<string> PerSessionCalls { get; } = new();
        public int BulkCalls { get; private set; }

        public RecordingCleanup(int perSessionResult = 0, bool throwOnPerSession = false)
        {
            _perSessionResult = perSessionResult;
            _throwOnPerSession = throwOnPerSession;
        }

        public Task<int> CleanupAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            PerSessionCalls.Add(sessionId);
            if (_throwOnPerSession)
                throw new InvalidOperationException("kaboom");
            return Task.FromResult(_perSessionResult);
        }

        public Task<SessionLockCleanupResult> CleanupAllAsync(CancellationToken cancellationToken = default)
        {
            BulkCalls++;
            return Task.FromResult(new SessionLockCleanupResult(0, 0));
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
        private readonly string _msg;
        public ThrowingLauncher(string msg) => _msg = msg;
        public Task<SessionLaunchResult> LaunchAsync(string sessionId, string? workingDirectory = null, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException(_msg);
        public Task<SessionLaunchResult> LaunchNewAsync(string? workingDirectory = null, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException(_msg);
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public FixedTimeProvider(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
