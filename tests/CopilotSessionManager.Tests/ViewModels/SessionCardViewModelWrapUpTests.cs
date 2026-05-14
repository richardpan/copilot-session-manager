using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CopilotSessionManager.Core.Models;
using CopilotSessionManager.Core.Sessions;
using CopilotSessionManager.Core.Settings;
using CopilotSessionManager.Services;
using CopilotSessionManager.ViewModels;
using FluentAssertions;
using Xunit;

namespace CopilotSessionManager.Tests.ViewModels;

/// <summary>
/// Tests for the V1.3 (#149) "📝 Wrap up" launcher button surface on
/// <see cref="SessionCardViewModel"/>.
/// </summary>
public class SessionCardViewModelWrapUpTests
{
    private static readonly DateTimeOffset Now = new(2026, 5, 8, 12, 0, 0, TimeSpan.Zero);

    private static Session BuildSession(
        SessionStatus status = SessionStatus.AwaitingInput,
        TimeSpan? idleFor = null,
        string id = "sess-1",
        string? summary = "Refactor the build pipeline",
        string? repository = "owner/repo",
        string? branch = "feat/build")
    {
        var updatedAt = Now - (idleFor ?? TimeSpan.FromHours(48));
        return new Session(
            Id: id,
            Cwd: @"C:\ws\repo",
            Repository: repository,
            Branch: branch,
            Summary: summary,
            HostType: "cli",
            CreatedAt: updatedAt.AddHours(-1),
            UpdatedAt: updatedAt,
            TurnCount: 1,
            Status: status,
            CopilotVersion: CopilotVersion.Zero,
            Locks: Array.Empty<SessionLockInfo>());
    }

    private static SessionCardViewModel CreateCard(
        Session? session = null,
        ISessionLauncher? launcher = null,
        IWrapUpStateStore? wrapStore = null,
        IClipboardService? clipboard = null,
        AppSettings? settings = null) =>
        new(
            session ?? BuildSession(),
            SessionType.Exploratory,
            new FixedTimeProvider(Now),
            modelCatalog: null, costCalculator: null,
            fileLauncher: null, lockCleanup: null,
            sessionLauncher: launcher, logger: null,
            openMergeWizard: null, issueLinks: null,
            runningSessions: null, windowActivator: null,
            displayNameStore: null, displayNameOverride: null,
            deletionService: null, confirmDelete: null,
            starStore: null, isStarred: false,
            onDeleted: null,
            docFreshness: null,
            readmeService: null,
            wrapUpStateStore: wrapStore,
            clipboardService: clipboard,
            appSettings: settings);

    private static AppSettings DefaultSettings() => new() { WrapUpAfterHours = 24 };

    // ---- IsWrapUpDue projection ----

    [Theory]
    [InlineData(SessionStatus.Working)]
    [InlineData(SessionStatus.Inactive)]
    [InlineData(SessionStatus.Orphaned)]
    [InlineData(SessionStatus.AwaitingApproval)]
    public void IsWrapUpDue_False_WhenStatusIsNotIdleOrAwaitingInput(SessionStatus status)
    {
        var card = CreateCard(
            session: BuildSession(status: status, idleFor: TimeSpan.FromHours(48)),
            launcher: new RecordingLauncher(),
            wrapStore: new FakeWrapStore(),
            clipboard: new RecordingClipboard(),
            settings: DefaultSettings());

        card.IsWrapUpDue.Should().BeFalse();
    }

    [Fact]
    public void IsWrapUpDue_False_WhenIdleBelowThreshold()
    {
        var card = CreateCard(
            session: BuildSession(status: SessionStatus.Idle, idleFor: TimeSpan.FromHours(2)),
            launcher: new RecordingLauncher(),
            wrapStore: new FakeWrapStore(),
            clipboard: new RecordingClipboard(),
            settings: DefaultSettings());

        card.IsWrapUpDue.Should().BeFalse();
    }

    [Fact]
    public void IsWrapUpDue_True_WhenAwaitingInputAndPastThreshold_AndNoPriorRequest()
    {
        var card = CreateCard(
            session: BuildSession(status: SessionStatus.AwaitingInput, idleFor: TimeSpan.FromHours(48)),
            launcher: new RecordingLauncher(),
            wrapStore: new FakeWrapStore(),
            clipboard: new RecordingClipboard(),
            settings: DefaultSettings());

        card.IsWrapUpDue.Should().BeTrue();
    }

    [Fact]
    public void IsWrapUpDue_True_WhenIdleAndPastThreshold_AndNoPriorRequest()
    {
        var card = CreateCard(
            session: BuildSession(status: SessionStatus.Idle, idleFor: TimeSpan.FromHours(48)),
            launcher: new RecordingLauncher(),
            wrapStore: new FakeWrapStore(),
            clipboard: new RecordingClipboard(),
            settings: DefaultSettings());

        card.IsWrapUpDue.Should().BeTrue();
    }

    [Fact]
    public void IsWrapUpDue_False_WhenWrapUpRequestedAtIsAfterUpdatedAt()
    {
        var session = BuildSession(idleFor: TimeSpan.FromHours(48));
        var card = CreateCard(
            session: session,
            launcher: new RecordingLauncher(),
            wrapStore: new FakeWrapStore(),
            clipboard: new RecordingClipboard(),
            settings: DefaultSettings());

        // Wrap-up was requested AFTER the session's last UpdatedAt.
        card.SetWrapUpRequestedAt(session.UpdatedAt.AddMinutes(5));
        card.IsWrapUpDue.Should().BeFalse();
    }

    [Fact]
    public void IsWrapUpDue_True_WhenWrapUpRequestedAtIsBeforeUpdatedAt()
    {
        var session = BuildSession(idleFor: TimeSpan.FromHours(48));
        var card = CreateCard(
            session: session,
            launcher: new RecordingLauncher(),
            wrapStore: new FakeWrapStore(),
            clipboard: new RecordingClipboard(),
            settings: DefaultSettings());

        // Wrap-up was requested in a previous session-state, before the
        // user re-engaged and bumped UpdatedAt.
        card.SetWrapUpRequestedAt(session.UpdatedAt.AddMinutes(-5));
        card.IsWrapUpDue.Should().BeTrue();
    }

    [Fact]
    public void IsWrapUpDue_False_WhenWrapStoreIsNull()
    {
        var card = CreateCard(
            session: BuildSession(idleFor: TimeSpan.FromHours(48)),
            launcher: new RecordingLauncher(),
            wrapStore: null,
            clipboard: new RecordingClipboard(),
            settings: DefaultSettings());

        card.IsWrapUpDue.Should().BeFalse();
    }

    [Fact]
    public void IsWrapUpDue_False_WhenAppSettingsIsNull()
    {
        var card = CreateCard(
            session: BuildSession(idleFor: TimeSpan.FromHours(48)),
            launcher: new RecordingLauncher(),
            wrapStore: new FakeWrapStore(),
            clipboard: new RecordingClipboard(),
            settings: null);

        card.IsWrapUpDue.Should().BeFalse();
    }

    [Fact]
    public void IsWrapUpDue_False_WhenWrapUpAfterHoursIsZeroOrNegative()
    {
        var disabled = new AppSettings { WrapUpAfterHours = 0 };
        var card = CreateCard(
            session: BuildSession(idleFor: TimeSpan.FromHours(48)),
            launcher: new RecordingLauncher(),
            wrapStore: new FakeWrapStore(),
            clipboard: new RecordingClipboard(),
            settings: disabled);

        card.IsWrapUpDue.Should().BeFalse();
    }

    // ---- WrapUpCommand ----

    [Fact]
    public async Task WrapUpCommand_CopiesSubstitutedPromptToClipboard_LaunchesAndPersists()
    {
        var session = BuildSession(idleFor: TimeSpan.FromHours(48));
        var launcher = new RecordingLauncher();
        var clipboard = new RecordingClipboard();
        var store = new FakeWrapStore();
        var settings = new AppSettings
        {
            WrapUpAfterHours = 24,
            WrapUpPromptTemplate = "id={sessionId} repo={repository} branch={branch}",
        };

        var card = CreateCard(session, launcher, store, clipboard, settings);
        await card.WrapUpCommand.ExecuteAsync(null);

        clipboard.LastText.Should().Be("id=sess-1 repo=owner/repo branch=feat/build");
        launcher.Calls.Should().HaveCount(1);
        store.Marks.Should().ContainSingle(m => m.SessionId == session.Id);
        card.LastActionMessage.Should().StartWith("Wrap-up prompt copied");
    }

    [Fact]
    public async Task WrapUpCommand_AbortsBeforeLaunch_WhenClipboardThrows()
    {
        var session = BuildSession(idleFor: TimeSpan.FromHours(48));
        var launcher = new RecordingLauncher();
        var clipboard = new ThrowingClipboard("clipboard busy");
        var store = new FakeWrapStore();
        var settings = DefaultSettings();

        var card = CreateCard(session, launcher, store, clipboard, settings);
        await card.WrapUpCommand.ExecuteAsync(null);

        launcher.Calls.Should().BeEmpty("the clipboard failure must short-circuit the flow");
        store.Marks.Should().BeEmpty();
        card.LastActionMessage.Should().Contain("Could not copy wrap-up prompt").And.Contain("clipboard busy");
    }

    [Fact]
    public void WrapUpCommand_CanExecute_FalseWhenIsWrapUpDueFalse()
    {
        var card = CreateCard(
            session: BuildSession(status: SessionStatus.Working, idleFor: TimeSpan.FromHours(48)),
            launcher: new RecordingLauncher(),
            wrapStore: new FakeWrapStore(),
            clipboard: new RecordingClipboard(),
            settings: DefaultSettings());

        card.WrapUpCommand.CanExecute(null).Should().BeFalse();
    }

    // ---- helpers ----

    private sealed class RecordingLauncher : ISessionLauncher
    {
        public List<(string SessionId, string? Cwd)> Calls { get; } = new();
        public Task<SessionLaunchResult> LaunchAsync(string sessionId, string? workingDirectory = null, CancellationToken cancellationToken = default)
        {
            Calls.Add((sessionId, workingDirectory));
            return Task.FromResult(new SessionLaunchResult(99, "pwsh.exe", "copilot --resume " + sessionId, workingDirectory ?? ""));
        }
        public Task<SessionLaunchResult> LaunchNewAsync(string? workingDirectory = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new SessionLaunchResult(100, "pwsh.exe", "copilot", workingDirectory ?? ""));
    }

    private sealed class RecordingClipboard : IClipboardService
    {
        public string? LastText { get; private set; }
        public void SetText(string text) => LastText = text;
    }

    private sealed class ThrowingClipboard : IClipboardService
    {
        private readonly string _message;
        public ThrowingClipboard(string message) => _message = message;
        public void SetText(string text) => throw new InvalidOperationException(_message);
    }

    private sealed class FakeWrapStore : IWrapUpStateStore
    {
        private readonly Dictionary<string, DateTimeOffset> _data = new(StringComparer.OrdinalIgnoreCase);
        public List<(string SessionId, DateTimeOffset RequestedAt)> Marks { get; } = new();

        public event EventHandler<WrapUpStateChangedEventArgs>? WrapUpStateChanged;

        public Task<DateTimeOffset?> GetRequestedAtAsync(string sessionId, CancellationToken cancellationToken = default)
            => Task.FromResult(_data.TryGetValue(sessionId, out var v) ? (DateTimeOffset?)v : null);

        public Task<IReadOnlyDictionary<string, DateTimeOffset>> GetAllAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyDictionary<string, DateTimeOffset>>(
                new Dictionary<string, DateTimeOffset>(_data, StringComparer.OrdinalIgnoreCase));

        public Task MarkRequestedAsync(string sessionId, DateTimeOffset requestedAt, CancellationToken cancellationToken = default)
        {
            _data[sessionId] = requestedAt;
            Marks.Add((sessionId, requestedAt));
            WrapUpStateChanged?.Invoke(this, new WrapUpStateChangedEventArgs(sessionId, requestedAt));
            return Task.CompletedTask;
        }

        public Task ClearAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            if (_data.Remove(sessionId))
            {
                WrapUpStateChanged?.Invoke(this, new WrapUpStateChangedEventArgs(sessionId, requestedAt: null));
            }
            return Task.CompletedTask;
        }
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public FixedTimeProvider(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
