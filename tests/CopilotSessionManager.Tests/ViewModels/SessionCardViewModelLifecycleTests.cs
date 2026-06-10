using System;
using System.Threading;
using System.Threading.Tasks;
using CopilotSessionManager.Core.Models;
using CopilotSessionManager.Core.Sessions;
using CopilotSessionManager.ViewModels;
using FluentAssertions;
using Xunit;

namespace CopilotSessionManager.Tests.ViewModels;

/// <summary>
/// Tests covering the user-controlled Open/Closed lifecycle pill: ctor
/// hydration, toggle persistence, idempotent ApplyLifecycleState, and
/// command CanExecute when no store is wired.
/// </summary>
public class SessionCardViewModelLifecycleTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    private static Session BuildSession() => new(
        Id: "sess-lifecycle",
        Cwd: @"C:\ws\repo",
        Repository: "owner/repo",
        Branch: "main",
        Summary: "lifecycle pill test",
        HostType: "cli",
        CreatedAt: Now.AddMinutes(-10),
        UpdatedAt: Now,
        TurnCount: 1,
        Status: SessionStatus.Idle,
        CopilotVersion: CopilotVersion.Zero,
        Locks: Array.Empty<SessionLockInfo>());

    [Fact]
    public void Defaults_to_open_when_no_store_wired()
    {
        var card = CreateCard(store: null, initial: SessionLifecycleState.Open);

        card.Lifecycle.Should().Be(SessionLifecycleState.Open);
        card.IsLifecycleClosed.Should().BeFalse();
        card.LifecyclePillText.Should().Be("Open");
        card.ToggleLifecycleCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public void Reflects_initial_closed_state_from_ctor()
    {
        var card = CreateCard(store: new FakeStore(), initial: SessionLifecycleState.Closed);

        card.IsLifecycleClosed.Should().BeTrue();
        card.LifecyclePillText.Should().Be("Closed");
        card.ToggleLifecycleCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public async Task ToggleLifecycleCommand_persists_and_updates_state()
    {
        var store = new FakeStore();
        var card = CreateCard(store, initial: SessionLifecycleState.Open);

        await card.ToggleLifecycleCommand.ExecuteAsync(null);

        card.Lifecycle.Should().Be(SessionLifecycleState.Closed);
        store.LastState.Should().Be(SessionLifecycleState.Closed);
        store.LastSessionId.Should().Be(card.Id);

        await card.ToggleLifecycleCommand.ExecuteAsync(null);

        card.Lifecycle.Should().Be(SessionLifecycleState.Open);
        store.LastState.Should().Be(SessionLifecycleState.Open);
    }

    [Fact]
    public async Task Toggle_raises_lifecycle_property_changes()
    {
        var card = CreateCard(store: new FakeStore(), initial: SessionLifecycleState.Open);
        var changes = new System.Collections.Generic.List<string?>();
        card.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        await card.ToggleLifecycleCommand.ExecuteAsync(null);

        card.IsLifecycleClosed.Should().BeTrue();
        changes.Should().Contain(nameof(SessionCardViewModel.Lifecycle));
        changes.Should().Contain(nameof(SessionCardViewModel.IsLifecycleClosed));
        changes.Should().Contain(nameof(SessionCardViewModel.LifecyclePillText));
        changes.Should().Contain(nameof(SessionCardViewModel.LifecycleTooltip));
    }

    private static SessionCardViewModel CreateCard(ISessionLifecycleStore? store, SessionLifecycleState initial) =>
        new(
            model: BuildSession(),
            label: SessionType.Exploratory,
            timeProvider: TimeProvider.System,
            modelCatalog: null,
            costCalculator: null,
            fileLauncher: null,
            lockCleanup: null,
            sessionLauncher: null,
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
            readmeService: null,
            wrapUpStateStore: null,
            clipboardService: null,
            appSettings: null,
            openEmbeddedTerminal: null,
            lifecycleStore: store,
            lifecycle: initial);

    private sealed class FakeStore : ISessionLifecycleStore
    {
        public string? LastSessionId { get; private set; }
        public SessionLifecycleState LastState { get; private set; }

        public event EventHandler<SessionLifecycleChangedEventArgs>? LifecycleChanged;

        public Task<SessionLifecycleState> GetAsync(string sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult(LastSessionId == sessionId ? LastState : SessionLifecycleState.Open);

        public Task<System.Collections.Generic.IReadOnlySet<string>> GetClosedAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<System.Collections.Generic.IReadOnlySet<string>>(new System.Collections.Generic.HashSet<string>());

        public Task SetAsync(string sessionId, SessionLifecycleState state, CancellationToken cancellationToken = default)
        {
            LastSessionId = sessionId;
            LastState = state;
            LifecycleChanged?.Invoke(this, new SessionLifecycleChangedEventArgs(sessionId, state));
            return Task.CompletedTask;
        }
    }
}
