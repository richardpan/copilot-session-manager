using System;
using System.Collections.Concurrent;
using System.Threading;
using CopilotSessionManager.Terminal.Hosting;

namespace CopilotSessionManager.Terminal.Hosting.Tests;

/// <summary>
/// Test dispatcher that queues callbacks and runs them on whichever
/// thread calls <see cref="Pump"/>. Lets tests precisely control when
/// the parser sees background-thread output (so assertions don't race
/// the reader task).
/// </summary>
internal sealed class QueuedDispatcher : ITerminalDispatcher
{
    private readonly BlockingCollection<Action> _queue = new();

    public int PostedCount { get; private set; }
    public int RanCount { get; private set; }

    public void Post(Action action)
    {
        PostedCount++;
        _queue.Add(action);
    }

    /// <summary>
    /// Drain queued callbacks for up to <paramref name="timeoutMs"/> ms or
    /// until <paramref name="stop"/> returns true, whichever comes first.
    /// </summary>
    public void Pump(int timeoutMs, Func<bool>? stop = null)
    {
        var deadline = Environment.TickCount + timeoutMs;
        while (Environment.TickCount < deadline)
        {
            if (stop is not null && stop())
            {
                return;
            }
            if (_queue.TryTake(out var action, 25))
            {
                RanCount++;
                action();
            }
        }
    }
}
