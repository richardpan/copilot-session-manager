using System;
using System.Collections.Generic;
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

public class SessionsViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 5, 8, 12, 0, 0, TimeSpan.Zero);

    private static Session Build(
        string id,
        SessionStatus status,
        DateTimeOffset? updatedAt = null) =>
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
            Locks: Array.Empty<SessionLockInfo>());

    private static (SessionsViewModel vm, FakeDiscoveryService disc) CreateSut(
        IEnumerable<Session>? initial = null,
        bool startWatcher = true)
    {
        var tp = new FixedTimeProvider(Now);
        var disc = new FakeDiscoveryService(initial?.ToArray() ?? Array.Empty<Session>());
        var vm = new SessionsViewModel(disc, new SyncDispatcher(), tp, NullLogger<SessionsViewModel>.Instance);
        if (startWatcher)
        {
            vm.InitializeAsync().GetAwaiter().GetResult();
        }
        return (vm, disc);
    }

    [Fact]
    public void Defaults_AreEmpty_AndShowInactiveTrue()
    {
        var (vm, _) = CreateSut(startWatcher: false);
        vm.ShowInactive.Should().BeTrue();
        vm.Sessions.Should().BeEmpty();
        vm.VisibleSessions.Should().BeEmpty();
        vm.TotalCount.Should().Be(0);
        vm.ActiveCount.Should().Be(0);
    }

    [Fact]
    public async Task InitializeAsync_PopulatesSessionsFromInitialScan()
    {
        var (vm, _) = CreateSut(new[]
        {
            Build("a", SessionStatus.Working),
            Build("b", SessionStatus.Idle),
        });

        vm.Sessions.Should().HaveCount(2);
        vm.TotalCount.Should().Be(2);
        vm.ActiveCount.Should().Be(2);
        await vm.DisposeAsync();
    }

    [Fact]
    public async Task InitializeAsync_IsIdempotent()
    {
        var (vm, disc) = CreateSut(new[] { Build("a", SessionStatus.Working) });
        var before = disc.StartCalls;
        await vm.InitializeAsync();
        disc.StartCalls.Should().Be(before);
        await vm.DisposeAsync();
    }

    [Fact]
    public async Task SessionsChangedEvent_TriggersInPlaceUpdate()
    {
        var (vm, disc) = CreateSut(new[] { Build("a", SessionStatus.Idle) });
        var card = vm.Sessions[0];

        disc.RaiseChanged(new[] { Build("a", SessionStatus.Working) });

        vm.Sessions.Should().HaveCount(1);
        vm.Sessions[0].Should().BeSameAs(card, because: "diff-merge keeps the same card instance for the same id");
        vm.Sessions[0].Status.Should().Be(SessionStatus.Working);
        await vm.DisposeAsync();
    }

    [Fact]
    public async Task SessionsChangedEvent_AddsAndRemovesByDiff()
    {
        var (vm, disc) = CreateSut(new[]
        {
            Build("a", SessionStatus.Idle),
            Build("b", SessionStatus.Working),
        });

        disc.RaiseChanged(new[]
        {
            Build("a", SessionStatus.Idle),
            Build("c", SessionStatus.Idle),
        });

        vm.Sessions.Select(s => s.Id).Should().BeEquivalentTo(new[] { "a", "c" });
        await vm.DisposeAsync();
    }

    [Fact]
    public async Task ShowInactive_False_HidesInactiveAndOrphaned()
    {
        var (vm, _) = CreateSut(new[]
        {
            Build("w", SessionStatus.Working),
            Build("i", SessionStatus.Idle),
            Build("x", SessionStatus.Inactive),
            Build("o", SessionStatus.Orphaned),
        });

        vm.VisibleSessions.Should().HaveCount(4);

        vm.ShowInactive = false;

        vm.VisibleSessions.Select(s => s.Id).Should().BeEquivalentTo(new[] { "w", "i" });
        await vm.DisposeAsync();
    }

    [Fact]
    public async Task ResortInPlace_PutsAwaitingApprovalFirst()
    {
        var (vm, disc) = CreateSut(new[]
        {
            Build("idle", SessionStatus.Idle),
            Build("inactive", SessionStatus.Inactive),
        });

        disc.RaiseChanged(new[]
        {
            Build("idle", SessionStatus.Idle),
            Build("inactive", SessionStatus.Inactive),
            Build("urgent", SessionStatus.AwaitingApproval),
        });

        vm.Sessions[0].Id.Should().Be("urgent");
        await vm.DisposeAsync();
    }

    [Fact]
    public async Task ActiveCount_ExcludesInactiveAndOrphaned()
    {
        var (vm, _) = CreateSut(new[]
        {
            Build("w", SessionStatus.Working),
            Build("i", SessionStatus.Idle),
            Build("x", SessionStatus.Inactive),
            Build("o", SessionStatus.Orphaned),
        });

        vm.ActiveCount.Should().Be(2);
        vm.TotalCount.Should().Be(4);
        await vm.DisposeAsync();
    }

    [Fact]
    public async Task DisposeAsync_UnsubscribesAndStopsWatcher()
    {
        var (vm, disc) = CreateSut(new[] { Build("a", SessionStatus.Idle) });
        await vm.DisposeAsync();
        disc.StopCalls.Should().Be(1);

        // Subsequent events should not crash or update collections.
        disc.RaiseChanged(new[] { Build("b", SessionStatus.Idle) });
        vm.Sessions.Select(s => s.Id).Should().BeEquivalentTo(new[] { "a" });
    }

    private sealed class FakeDiscoveryService : ISessionDiscoveryService
    {
        private List<Session> _current;

        public FakeDiscoveryService(IReadOnlyList<Session> initial)
        {
            _current = new List<Session>(initial);
        }

        public IReadOnlyList<Session> CurrentSessions => _current;
        public event EventHandler<SessionsChangedEventArgs>? SessionsChanged;
        public int StartCalls { get; private set; }
        public int StopCalls { get; private set; }

        public Task<IReadOnlyList<Session>> ScanAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Session>>(_current);

        public Task StartWatchingAsync(CancellationToken cancellationToken = default)
        {
            StartCalls++;
            return Task.CompletedTask;
        }

        public Task StopWatchingAsync()
        {
            StopCalls++;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            SessionsChanged = null;
            return ValueTask.CompletedTask;
        }

        public void RaiseChanged(IReadOnlyList<Session> snapshot)
        {
            _current = new List<Session>(snapshot);
            SessionsChanged?.Invoke(this, new SessionsChangedEventArgs(snapshot));
        }
    }

    private sealed class SyncDispatcher : IUiDispatcher
    {
        public void Post(Action action) => action();
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public FixedTimeProvider(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
