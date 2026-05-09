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

    private static (SessionsViewModel vm, FakeDiscoveryService disc, FakeLabelStore labels,
        FakeReadmeService readme, FakeFileLauncher launcher) CreateSut(
        IEnumerable<Session>? initial = null,
        bool startWatcher = true,
        IEnumerable<KeyValuePair<string, SessionType>>? seedLabels = null)
    {
        var tp = new FixedTimeProvider(Now);
        var disc = new FakeDiscoveryService(initial?.ToArray() ?? Array.Empty<Session>());
        var labels = new FakeLabelStore();
        if (seedLabels is not null)
        {
            foreach (var kv in seedLabels)
            {
                labels.Seed(kv.Key, kv.Value);
            }
        }
        var readme = new FakeReadmeService();
        var launcher = new FakeFileLauncher();
        var vm = new SessionsViewModel(
            disc, labels, readme, launcher, new SyncDispatcher(), tp,
            NullLogger<SessionsViewModel>.Instance);
        if (startWatcher)
        {
            vm.InitializeAsync().GetAwaiter().GetResult();
        }
        return (vm, disc, labels, readme, launcher);
    }

    [Fact]
    public void Defaults_AreEmpty_AndShowInactiveTrue()
    {
        var (vm, _, _, _, _) = CreateSut(startWatcher: false);
        vm.ShowInactive.Should().BeTrue();
        vm.Sessions.Should().BeEmpty();
        vm.VisibleSessions.Should().BeEmpty();
        vm.TotalCount.Should().Be(0);
        vm.ActiveCount.Should().Be(0);
        vm.LabelFilters.Should().HaveCount(8, because: "one chip per SessionType");
    }

    [Fact]
    public async Task InitializeAsync_PopulatesSessionsFromInitialScan()
    {
        var (vm, _, _, _, _) = CreateSut(new[]
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
    public async Task InitializeAsync_AppliesStoredLabels()
    {
        var (vm, _, _, _, _) = CreateSut(
            new[] { Build("a", SessionStatus.Idle), Build("b", SessionStatus.Idle) },
            seedLabels: new Dictionary<string, SessionType> { ["a"] = SessionType.Bug });

        vm.Sessions.Single(s => s.Id == "a").Label.Should().Be(SessionType.Bug);
        vm.Sessions.Single(s => s.Id == "b").Label.Should().Be(SessionType.Exploratory);
        await vm.DisposeAsync();
    }

    [Fact]
    public async Task InitializeAsync_IsIdempotent()
    {
        var (vm, disc, _, _, _) = CreateSut(new[] { Build("a", SessionStatus.Working) });
        var before = disc.StartCalls;
        await vm.InitializeAsync();
        disc.StartCalls.Should().Be(before);
        await vm.DisposeAsync();
    }

    [Fact]
    public async Task SessionsChangedEvent_TriggersInPlaceUpdate()
    {
        var (vm, disc, _, _, _) = CreateSut(new[] { Build("a", SessionStatus.Idle) });
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
        var (vm, disc, _, _, _) = CreateSut(new[]
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
        var (vm, _, _, _, _) = CreateSut(new[]
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
        var (vm, disc, _, _, _) = CreateSut(new[]
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
        var (vm, _, _, _, _) = CreateSut(new[]
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
    public async Task SetLabelAsync_PersistsAndUpdatesCard()
    {
        var (vm, _, labels, _, _) = CreateSut(new[] { Build("a", SessionStatus.Idle) });
        var card = vm.Sessions[0];

        await vm.SetLabelAsync(card, SessionType.Bug);

        labels.GetSeed("a").Should().Be(SessionType.Bug);
        card.Label.Should().Be(SessionType.Bug);
        await vm.DisposeAsync();
    }

    [Fact]
    public async Task LabelChangedFromStore_UpdatesMatchingCard()
    {
        var (vm, _, labels, _, _) = CreateSut(new[] { Build("a", SessionStatus.Idle) });
        var card = vm.Sessions[0];

        labels.RaiseLabelChanged("a", SessionType.Refactor);

        card.Label.Should().Be(SessionType.Refactor);
        await vm.DisposeAsync();
    }

    [Fact]
    public async Task LabelFilter_HidesUncheckedLabels()
    {
        var (vm, _, _, _, _) = CreateSut(
            new[]
            {
                Build("a", SessionStatus.Idle),
                Build("b", SessionStatus.Idle),
            },
            seedLabels: new Dictionary<string, SessionType>
            {
                ["a"] = SessionType.Bug,
                ["b"] = SessionType.Feature,
            });

        vm.VisibleSessions.Should().HaveCount(2);

        var bugChip = vm.LabelFilters.Single(c => c.Type == SessionType.Bug);
        bugChip.IsVisible = false;

        vm.VisibleSessions.Select(s => s.Id).Should().BeEquivalentTo(new[] { "b" });

        bugChip.IsVisible = true;
        vm.VisibleSessions.Should().HaveCount(2);

        await vm.DisposeAsync();
    }

    [Fact]
    public async Task TierFilter_HidesUncheckedTiers()
    {
        // All sessions land under "Unknown" tier because Build() doesn't
        // populate ModelInfo. Toggling Unknown off should hide all of them;
        // toggling another tier off should not affect them.
        var (vm, _, _, _, _) = CreateSut(new[]
        {
            Build("a", SessionStatus.Idle),
            Build("b", SessionStatus.Idle),
        });

        vm.TierFilters.Should().Contain(c => c.Tier == ModelTier.Unknown);
        var unknown = vm.TierFilters.Single(c => c.Tier == ModelTier.Unknown);
        var premium = vm.TierFilters.Single(c => c.Tier == ModelTier.Premium);

        vm.VisibleSessions.Should().HaveCount(2);

        premium.IsVisible = false;
        vm.VisibleSessions.Should().HaveCount(2, because: "no premium sessions to hide");

        unknown.IsVisible = false;
        vm.VisibleSessions.Should().BeEmpty();

        unknown.IsVisible = true;
        vm.VisibleSessions.Should().HaveCount(2);

        await vm.DisposeAsync();
    }

    [Fact]
    public async Task DisposeAsync_UnsubscribesFromBothEvents()
    {
        var (vm, disc, labels, _, _) = CreateSut(new[] { Build("a", SessionStatus.Idle) });
        await vm.DisposeAsync();
        disc.StopCalls.Should().Be(1);

        // Subsequent events should not crash or update collections.
        disc.RaiseChanged(new[] { Build("b", SessionStatus.Idle) });
        labels.RaiseLabelChanged("a", SessionType.Bug);

        vm.Sessions.Select(s => s.Id).Should().BeEquivalentTo(new[] { "a" });
        vm.Sessions[0].Label.Should().Be(SessionType.Exploratory);
    }

    [Fact]
    public async Task OpenReadmeAsync_NullCard_DoesNothing()
    {
        var (vm, _, _, readme, launcher) = CreateSut();
        await vm.OpenReadmeAsync(null);
        readme.EnsureCalls.Should().Be(0);
        launcher.Calls.Should().BeEmpty();
        await vm.DisposeAsync();
    }

    [Fact]
    public async Task OpenReadmeAsync_EnsuresThenLaunches_WithCardLabel()
    {
        var (vm, _, _, readme, launcher) = CreateSut(
            new[] { Build("a", SessionStatus.Idle) },
            seedLabels: new Dictionary<string, SessionType> { ["a"] = SessionType.Bug });
        var card = vm.Sessions[0];

        await vm.OpenReadmeAsync(card);

        readme.EnsureCalls.Should().Be(1);
        readme.LastLabel.Should().Be(SessionType.Bug);
        readme.LastSession?.Id.Should().Be("a");
        launcher.Calls.Should().ContainSingle().Which.Should().EndWith("SESSION-README.md");
        await vm.DisposeAsync();
    }

    [Fact]
    public async Task OpenReadmeAsync_StoreFailure_SurfacesViaStatusMessage_AndDoesNotLaunch()
    {
        var (vm, _, _, readme, launcher) = CreateSut(new[] { Build("a", SessionStatus.Idle) });
        readme.ThrowOnEnsure = true;
        var card = vm.Sessions[0];

        await vm.OpenReadmeAsync(card);

        launcher.Calls.Should().BeEmpty();
        vm.StatusMessage.Should().Contain("Could not open README");
        await vm.DisposeAsync();
    }

    private sealed class FakeReadmeService : ISessionReadmeService
    {
        public int EnsureCalls { get; private set; }
        public Session? LastSession { get; private set; }
        public SessionType? LastLabel { get; private set; }
        public bool ThrowOnEnsure { get; set; }

        public string GetReadmePath(string sessionId) => $"/sessions/{sessionId}/SESSION-README.md";

        public Task<string> EnsureAsync(Session session, SessionType label, CancellationToken cancellationToken = default)
        {
            EnsureCalls++;
            LastSession = session;
            LastLabel = label;
            if (ThrowOnEnsure)
            {
                throw new InvalidOperationException("disk full");
            }
            return Task.FromResult(string.Empty);
        }
    }

    private sealed class FakeFileLauncher : IFileLauncher
    {
        public List<string> Calls { get; } = new();
        public Task OpenAsync(string path, CancellationToken cancellationToken = default)
        {
            Calls.Add(path);
            return Task.CompletedTask;
        }
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

    private sealed class FakeLabelStore : ISessionLabelStore
    {
        private readonly Dictionary<string, SessionType> _labels = new(StringComparer.OrdinalIgnoreCase);

        public event EventHandler<SessionLabelChangedEventArgs>? LabelChanged;

        public Task<SessionType> GetAsync(string sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult(_labels.TryGetValue(sessionId, out var t) ? t : SessionType.Exploratory);

        public Task<IReadOnlyDictionary<string, SessionType>> GetAllAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, SessionType>>(
                new Dictionary<string, SessionType>(_labels, StringComparer.OrdinalIgnoreCase));

        public Task SetAsync(string sessionId, SessionType type, CancellationToken cancellationToken = default)
        {
            var changed = !_labels.TryGetValue(sessionId, out var existing) || existing != type;
            _labels[sessionId] = type;
            if (changed)
            {
                LabelChanged?.Invoke(this, new SessionLabelChangedEventArgs(sessionId, type));
            }
            return Task.CompletedTask;
        }

        public Task RemoveAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            if (_labels.Remove(sessionId))
            {
                LabelChanged?.Invoke(this, new SessionLabelChangedEventArgs(sessionId, SessionType.Exploratory));
            }
            return Task.CompletedTask;
        }

        public void Seed(string sessionId, SessionType type) => _labels[sessionId] = type;

        public SessionType GetSeed(string sessionId) =>
            _labels.TryGetValue(sessionId, out var t) ? t : SessionType.Exploratory;

        public void RaiseLabelChanged(string sessionId, SessionType type) =>
            LabelChanged?.Invoke(this, new SessionLabelChangedEventArgs(sessionId, type));
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
