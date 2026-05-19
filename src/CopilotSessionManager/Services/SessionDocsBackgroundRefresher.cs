using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using CopilotSessionManager.Core.Models;
using CopilotSessionManager.Core.Sessions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CopilotSessionManager.Services;

/// <summary>
/// V1.5 (#196): hosts a background loop that keeps every session's
/// <c>SESSION-DOCS.html</c> fresh without requiring the user to click the
/// 📚 Docs button. Without this service the HTML view stayed stale
/// indefinitely after the V1.5 Docs button retarget at <c>plan.md</c>
/// — see PR #195.
/// </summary>
/// <remarks>
/// <para>
/// The loop is driven by three signals:
/// <list type="number">
///   <item>An initial sweep over <see cref="ISessionDiscoveryService.CurrentSessions"/> on startup.</item>
///   <item>Live updates from <see cref="ISessionDiscoveryService.SessionsChanged"/> — fires whenever the
///   on-disk session-state directory mutates.</item>
///   <item>A periodic sweep every <see cref="DefaultPeriod"/> so file mutations that don't surface as
///   discovery events (e.g. someone editing <c>SESSION-DOCS.md</c> outside csm) are still picked up
///   within bounded latency.</item>
/// </list>
/// </para>
/// <para>
/// All three signals enqueue work into an unbounded <see cref="Channel{T}"/>;
/// a single background worker drains the channel sequentially so disk
/// writes never overlap. A per-session cooldown of <see cref="DefaultCooldown"/>
/// suppresses redundant work when multiple signals fire in quick succession.
/// Calling <see cref="ISessionDocsService.EnsureAsync"/> is itself cheap when
/// nothing has actually changed (it only compares mtimes), so the worst case
/// of a sweep firing on an idle workspace is one stat-per-source.
/// </para>
/// </remarks>
public sealed class SessionDocsBackgroundRefresher : IHostedService, IAsyncDisposable
{
    /// <summary>How often the periodic safety-net sweep fires.</summary>
    public static readonly TimeSpan DefaultPeriod = TimeSpan.FromSeconds(60);

    /// <summary>Minimum time between back-to-back refreshes of the same session.</summary>
    public static readonly TimeSpan DefaultCooldown = TimeSpan.FromSeconds(15);

    private readonly ISessionDocsService _docs;
    private readonly ISessionDiscoveryService _discovery;
    private readonly TimeProvider _time;
    private readonly ILogger<SessionDocsBackgroundRefresher> _logger;
    private readonly TimeSpan _period;
    private readonly TimeSpan _cooldown;
    private readonly Channel<Session> _queue;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastRefreshUtc = new(StringComparer.Ordinal);

    private CancellationTokenSource? _stoppingCts;
    private Task? _worker;
    private ITimer? _periodicTimer;
    private bool _started;
    private bool _disposed;

    public SessionDocsBackgroundRefresher(
        ISessionDocsService docs,
        ISessionDiscoveryService discovery,
        TimeProvider time,
        ILogger<SessionDocsBackgroundRefresher> logger)
        : this(docs, discovery, time, logger, DefaultPeriod, DefaultCooldown)
    {
    }

    /// <summary>
    /// Test seam (and small power-user knob): lets callers shrink the
    /// periodic sweep period (pass <see cref="Timeout.InfiniteTimeSpan"/>
    /// to disable it) and the per-session cooldown. Production code
    /// should generally use the default constructor.
    /// </summary>
    public SessionDocsBackgroundRefresher(
        ISessionDocsService docs,
        ISessionDiscoveryService discovery,
        TimeProvider time,
        ILogger<SessionDocsBackgroundRefresher> logger,
        TimeSpan period,
        TimeSpan cooldown)
    {
        ArgumentNullException.ThrowIfNull(docs);
        ArgumentNullException.ThrowIfNull(discovery);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);

        _docs = docs;
        _discovery = discovery;
        _time = time;
        _logger = logger;
        _period = period;
        _cooldown = cooldown < TimeSpan.Zero ? TimeSpan.Zero : cooldown;

        _queue = Channel.CreateUnbounded<Session>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_started)
        {
            return Task.CompletedTask;
        }
        _started = true;

        _stoppingCts = new CancellationTokenSource();
        var workerToken = _stoppingCts.Token;
        _worker = Task.Run(() => RunWorkerAsync(workerToken), workerToken);

        _discovery.SessionsChanged += OnSessionsChanged;
        EnqueueAll(_discovery.CurrentSessions);

        if (_period > TimeSpan.Zero && _period != Timeout.InfiniteTimeSpan)
        {
            _periodicTimer = _time.CreateTimer(
                _ => EnqueueAll(_discovery.CurrentSessions),
                state: null,
                dueTime: _period,
                period: _period);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (!_started)
        {
            return;
        }

        _discovery.SessionsChanged -= OnSessionsChanged;

        if (_periodicTimer is not null)
        {
            _periodicTimer.Dispose();
            _periodicTimer = null;
        }

        _queue.Writer.TryComplete();
        _stoppingCts?.Cancel();

        if (_worker is not null)
        {
            try
            {
                await _worker.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Either the worker observed cancellation or our own
                // wait timed out — either way, shutdown proceeds.
            }
            _worker = null;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _stoppingCts?.Dispose();
        _stoppingCts = null;
    }

    /// <summary>
    /// Test seam: synchronously enqueue a session for refresh. Production
    /// code should not call this — it bypasses the discovery-event /
    /// initial-scan path that wires the queue normally.
    /// </summary>
    internal bool TryEnqueue(Session session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return _queue.Writer.TryWrite(session);
    }

    private void OnSessionsChanged(object? sender, SessionsChangedEventArgs e)
    {
        EnqueueAll(e.Sessions);
    }

    private void EnqueueAll(IReadOnlyList<Session> sessions)
    {
        if (sessions is null)
        {
            return;
        }

        foreach (var session in sessions)
        {
            if (session is null)
            {
                continue;
            }
            _queue.Writer.TryWrite(session);
        }
    }

    private async Task RunWorkerAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var session in _queue.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                if (string.IsNullOrWhiteSpace(session.Id))
                {
                    continue;
                }

                if (IsOnCooldown(session.Id))
                {
                    continue;
                }

                _lastRefreshUtc[session.Id] = _time.GetUtcNow();

                try
                {
                    await _docs.EnsureAsync(session, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Background docs refresh failed for session {Id}.",
                        session.Id);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Background docs refresher loop terminated unexpectedly.");
        }
    }

    private bool IsOnCooldown(string sessionId)
    {
        if (!_lastRefreshUtc.TryGetValue(sessionId, out var last))
        {
            return false;
        }

        return _time.GetUtcNow() - last < _cooldown;
    }
}
