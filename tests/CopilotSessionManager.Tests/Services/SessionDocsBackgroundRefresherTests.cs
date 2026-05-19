using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CopilotSessionManager.Core.Models;
using CopilotSessionManager.Core.Sessions;
using CopilotSessionManager.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CopilotSessionManager.Tests.Services;

/// <summary>
/// V1.5 (#196): cover the background loop that keeps SESSION-DOCS.html
/// fresh without requiring manual Docs-button clicks. The refresher is
/// triggered by three signals (initial scan, SessionsChanged events,
/// periodic sweep) and gates per-session work behind a cooldown.
/// </summary>
public sealed class SessionDocsBackgroundRefresherTests
{
    private static Session BuildSession(string id) => new(
        Id: id,
        Cwd: @"C:\ws\repo",
        Repository: "owner/repo",
        Branch: "main",
        Summary: "session " + id,
        HostType: "cli",
        CreatedAt: DateTimeOffset.UtcNow.AddHours(-1),
        UpdatedAt: DateTimeOffset.UtcNow,
        TurnCount: 1,
        Status: SessionStatus.Idle,
        CopilotVersion: CopilotVersion.Zero,
        Locks: Array.Empty<SessionLockInfo>());

    private static async Task<bool> WaitForCountAsync(Func<int> sampler, int target, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (DateTime.UtcNow < deadline)
        {
            if (sampler() >= target)
            {
                return true;
            }
            await Task.Delay(10);
        }
        return sampler() >= target;
    }

    [Fact]
    public async Task StartAsync_EnqueuesAllCurrentSessions_AndWorkerCallsEnsureForEach()
    {
        var docs = new RecordingDocsService();
        var discovery = new FakeDiscoveryService(new[] { BuildSession("a"), BuildSession("b") });
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);

        await using var sut = new SessionDocsBackgroundRefresher(
            docs, discovery, time, NullLogger<SessionDocsBackgroundRefresher>.Instance,
            period: Timeout.InfiniteTimeSpan, cooldown: TimeSpan.FromSeconds(15));

        await sut.StartAsync(CancellationToken.None);

        var hit = await WaitForCountAsync(() => docs.SessionsEnsured.Count, 2);
        hit.Should().BeTrue("StartAsync must enqueue every session in CurrentSessions");
        docs.SessionsEnsured.Should().BeEquivalentTo(new[] { "a", "b" });

        await sut.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task SessionsChanged_TriggersRefreshOfEachReportedSession()
    {
        var docs = new RecordingDocsService();
        var discovery = new FakeDiscoveryService(Array.Empty<Session>());
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);

        await using var sut = new SessionDocsBackgroundRefresher(
            docs, discovery, time, NullLogger<SessionDocsBackgroundRefresher>.Instance,
            period: Timeout.InfiniteTimeSpan, cooldown: TimeSpan.FromSeconds(15));

        await sut.StartAsync(CancellationToken.None);

        discovery.RaiseChanged(new[] { BuildSession("c"), BuildSession("d") });

        (await WaitForCountAsync(() => docs.SessionsEnsured.Count, 2)).Should().BeTrue();
        docs.SessionsEnsured.Should().Contain(new[] { "c", "d" });

        await sut.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Cooldown_SuppressesBackToBackRefreshesOfSameSession()
    {
        var docs = new RecordingDocsService();
        var discovery = new FakeDiscoveryService(Array.Empty<Session>());
        var start = DateTimeOffset.UtcNow;
        var time = new FakeTimeProvider(start);

        await using var sut = new SessionDocsBackgroundRefresher(
            docs, discovery, time, NullLogger<SessionDocsBackgroundRefresher>.Instance,
            period: Timeout.InfiniteTimeSpan, cooldown: TimeSpan.FromSeconds(30));

        await sut.StartAsync(CancellationToken.None);

        // First refresh runs.
        discovery.RaiseChanged(new[] { BuildSession("a") });
        (await WaitForCountAsync(() => docs.SessionsEnsured.Count, 1)).Should().BeTrue();

        // Within the cooldown window the second refresh must be dropped.
        discovery.RaiseChanged(new[] { BuildSession("a") });
        await Task.Delay(150);
        docs.SessionsEnsured.Count(id => id == "a").Should().Be(1,
            "the same session must not be refreshed twice within the cooldown window");

        // Once the cooldown elapses, a follow-up refresh runs again.
        time.Advance(TimeSpan.FromSeconds(31));
        discovery.RaiseChanged(new[] { BuildSession("a") });
        (await WaitForCountAsync(() => docs.SessionsEnsured.Count(id => id == "a"), 2)).Should().BeTrue();

        await sut.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StopAsync_CancelsTheWorker_AndUnsubscribesFromDiscovery()
    {
        var docs = new RecordingDocsService();
        var discovery = new FakeDiscoveryService(Array.Empty<Session>());
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);

        var sut = new SessionDocsBackgroundRefresher(
            docs, discovery, time, NullLogger<SessionDocsBackgroundRefresher>.Instance,
            period: Timeout.InfiniteTimeSpan, cooldown: TimeSpan.Zero);

        await sut.StartAsync(CancellationToken.None);
        await sut.StopAsync(CancellationToken.None);

        // After Stop, a SessionsChanged event must NOT trigger any further refreshes.
        discovery.RaiseChanged(new[] { BuildSession("late") });
        await Task.Delay(100);

        docs.SessionsEnsured.Should().NotContain("late",
            "the refresher must unsubscribe from SessionsChanged on Stop");

        await sut.DisposeAsync();
    }

    [Fact]
    public async Task EnsureAsync_FailuresAreLogged_ButDoNotStopTheWorker()
    {
        var docs = new RecordingDocsService { FailIds = new HashSet<string> { "boom" } };
        var discovery = new FakeDiscoveryService(Array.Empty<Session>());
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);

        await using var sut = new SessionDocsBackgroundRefresher(
            docs, discovery, time, NullLogger<SessionDocsBackgroundRefresher>.Instance,
            period: Timeout.InfiniteTimeSpan, cooldown: TimeSpan.Zero);

        await sut.StartAsync(CancellationToken.None);

        discovery.RaiseChanged(new[] { BuildSession("boom"), BuildSession("ok") });

        // The failing session is still recorded (cooldown captured), and the
        // worker survives to process the next one.
        (await WaitForCountAsync(() => docs.SessionsEnsured.Count, 2)).Should().BeTrue();
        docs.SessionsEnsured.Should().Contain("ok");

        await sut.StopAsync(CancellationToken.None);
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Test doubles
    // ─────────────────────────────────────────────────────────────────────

    private sealed class RecordingDocsService : ISessionDocsService
    {
        private readonly ConcurrentQueue<string> _ensured = new();
        public IReadOnlyList<string> SessionsEnsured => _ensured.ToArray();
        public HashSet<string> FailIds { get; init; } = new(StringComparer.Ordinal);

        public string GetDocsMarkdownPath(string sessionId) => $"/fake/{sessionId}/SESSION-DOCS.md";
        public string GetDocsHtmlPath(string sessionId) => $"/fake/{sessionId}/SESSION-DOCS.html";
        public string GetPlanMarkdownPath(string sessionId) => $"/fake/{sessionId}/plan.md";

        public Task<string> EnsureAsync(Session session, CancellationToken cancellationToken = default)
        {
            _ensured.Enqueue(session.Id);
            if (FailIds.Contains(session.Id))
            {
                throw new InvalidOperationException("simulated failure for " + session.Id);
            }
            return Task.FromResult(GetDocsHtmlPath(session.Id));
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

        public Task<IReadOnlyList<Session>> ScanAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<Session>>(_current);

        public Task StartWatchingAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopWatchingAsync() => Task.CompletedTask;

        public void RaiseChanged(IReadOnlyList<Session> snapshot)
        {
            _current = new List<Session>(snapshot);
            SessionsChanged?.Invoke(this, new SessionsChangedEventArgs(snapshot));
        }

        public ValueTask DisposeAsync()
        {
            SessionsChanged = null;
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// Minimal <see cref="TimeProvider"/> for cooldown gating in these tests.
    /// We intentionally never call <c>CreateTimer</c> from the refresher in
    /// tests (the period parameter is always <see cref="Timeout.InfiniteTimeSpan"/>),
    /// so falling through to the default base behaviour is fine.
    /// </summary>
    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _now;
        public FakeTimeProvider(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
    }
}
