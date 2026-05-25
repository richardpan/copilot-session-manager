using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CopilotSessionManager.Native;

namespace CopilotSessionManager.Terminal.Hosting;

/// <summary>
/// End-to-end terminal session façade. Composes a process (ConPTY or
/// in-memory fake), a <see cref="VtParser"/>, and a <see cref="ScreenBuffer"/>
/// into a single object that a host UI (the WPF <c>TerminalControl</c>)
/// can attach to. Phase 3E of epic #93.
/// </summary>
/// <remarks>
/// <para>Wiring:</para>
/// <list type="bullet">
///   <item>A background reader task pumps bytes from <c>process.OutputStream</c>
///   into a fixed-size buffer.</item>
///   <item>Each chunk is marshalled onto the UI thread via
///   <see cref="ITerminalDispatcher"/> before being fed to the parser,
///   so that <see cref="ScreenBuffer.ViewportInvalidated"/> always fires on
///   the same thread the renderer expects.</item>
///   <item><see cref="SendInput(ReadOnlySpan{byte})"/> writes synchronously
///   to <c>process.InputStream</c>; callers are expected to invoke it from
///   the UI thread (in response to keyboard / paste events).</item>
///   <item><see cref="Resize"/> updates both the underlying process and
///   the buffer.</item>
/// </list>
/// <para>
/// The session is single-owner: <see cref="Dispose"/> cancels the reader,
/// disposes the process, and raises <see cref="Exited"/> at most once.
/// </para>
/// </remarks>
public sealed class TerminalSession : IDisposable
{
    private const int ReadBufferSize = 4096;

    private readonly ITerminalProcess _process;
    private readonly ITerminalDispatcher _dispatcher;
    private readonly IVtParser _parser;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _readerTask;
    private readonly bool _ownsProcess;

    private int _exitedRaised;
    private int _disposed;
    private volatile bool _capturingHistory;
    private int _capturedDepth;
    private readonly HashSet<int> _capturedHashes = new();

    /// <summary>
    /// Build a session around the given <paramref name="process"/> with
    /// a fresh <see cref="ScreenBuffer"/> sized to <paramref name="rows"/>
    /// × <paramref name="cols"/>.
    /// </summary>
    /// <param name="process">
    /// The byte source / sink. Ownership transfers to the session by
    /// default; pass <paramref name="ownsProcess"/> = <c>false</c> if the
    /// caller wants to dispose it themselves.
    /// </param>
    /// <param name="dispatcher">UI-thread marshaller for parser callbacks.</param>
    /// <param name="rows">Initial row count. Must be &gt; 0.</param>
    /// <param name="cols">Initial column count. Must be &gt; 0.</param>
    /// <param name="ownsProcess">When true (default), disposing the session disposes the process.</param>
    public TerminalSession(ITerminalProcess process, ITerminalDispatcher dispatcher, int rows, int cols, bool ownsProcess = true)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(dispatcher);
        if (rows <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(rows), rows, "Must be > 0.");
        }
        if (cols <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(cols), cols, "Must be > 0.");
        }

        _process = process;
        _dispatcher = dispatcher;
        _ownsProcess = ownsProcess;
        Buffer = new ScreenBuffer(rows, cols);
        _parser = new VtParser(Buffer.Apply);
        _readerTask = Task.Run(() => ReadLoopAsync(_cts.Token));
    }

    /// <summary>
    /// Convenience overload that spawns <paramref name="commandLine"/> in
    /// a fresh <see cref="PseudoConsole"/> and wraps it in a
    /// <see cref="PseudoConsoleTerminalProcess"/>. Closes Phase 3E's
    /// "live validation over pwsh" path.
    /// </summary>
    public static TerminalSession Start(string commandLine, int rows, int cols, ITerminalDispatcher dispatcher, string? workingDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandLine);
        var process = PseudoConsoleTerminalProcess.Start(commandLine, (short)cols, (short)rows, workingDirectory);
        try
        {
            return new TerminalSession(process, dispatcher, rows, cols);
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }

    /// <summary>The screen buffer driven by the parser. Bind to a <c>TerminalControl.Buffer</c>.</summary>
    public ScreenBuffer Buffer { get; }

    /// <summary>True once the child has exited and the reader has drained the output pipe.</summary>
    public bool HasExited => _process.HasExited && _readerTask.IsCompleted;

    /// <summary>
    /// Raised exactly once on the dispatcher thread when the reader task
    /// detects EOF (i.e. the child exited and ConPTY closed our read end)
    /// or when <see cref="Dispose"/> is called.
    /// </summary>
    public event EventHandler? Exited;

    /// <summary>
    /// Forward <paramref name="bytes"/> straight to the child's stdin.
    /// Intended caller: the host control's <c>InputProduced</c> handler.
    /// </summary>
    public void SendInput(ReadOnlySpan<byte> bytes)
    {
        if (_disposed != 0)
        {
            throw new ObjectDisposedException(nameof(TerminalSession));
        }
        if (bytes.IsEmpty)
        {
            return;
        }

        // Stream.Write only takes byte[] / ReadOnlySpan; the latter is fine.
        _process.InputStream.Write(bytes);
        _process.InputStream.Flush();
    }

    /// <summary>Resize both the underlying pseudo-console and the screen buffer.</summary>
    public void Resize(int rows, int cols)
    {
        if (_disposed != 0)
        {
            throw new ObjectDisposedException(nameof(TerminalSession));
        }
        // Suppress external resizes while a history capture is in progress.
        if (_capturingHistory)
        {
            return;
        }
        if (rows <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(rows), rows, "Must be > 0.");
        }
        if (cols <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(cols), cols, "Must be > 0.");
        }

        _process.Resize((short)cols, (short)rows);
        // Marshal the buffer resize onto the UI thread so it interleaves
        // correctly with parser-driven mutations.
        _dispatcher.Post(() => Buffer.Resize(rows, cols));
    }

    private const int HistoryChunkSize = 1000;
    private const int HistoryMaxDepth = 10000;

    /// <summary>
    /// Incrementally expand the terminal viewport to capture more
    /// scrollback history. Each call extends the capture by
    /// <see cref="HistoryChunkSize"/> rows (up to
    /// <see cref="HistoryMaxDepth"/>). The TUI app re-renders at the
    /// larger size, and the newly visible rows are pushed to scrollback
    /// before the viewport shrinks back. Safe to call from any thread;
    /// concurrent calls are serialised via <see cref="_capturingHistory"/>.
    /// </summary>
    public async Task CaptureMoreHistoryAsync()
    {
        if (_disposed != 0 || _capturingHistory)
        {
            return;
        }

        var nextDepth = _capturedDepth + HistoryChunkSize;
        if (nextDepth > HistoryMaxDepth)
        {
            return; // already at maximum capture depth
        }

        var originalRows = Buffer.Rows;
        var cols = Buffer.Columns;
        if (nextDepth <= originalRows || cols <= 0)
        {
            return;
        }

        _capturingHistory = true;
        try
        {
            // 1. Seed hashes with current live viewport content so we
            //    never push rows the user can already see.
            var seedDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _dispatcher.Post(() =>
            {
                try
                {
                    for (var r = 0; r < originalRows; r++)
                    {
                        var text = Buffer.GetRowText(r).TrimEnd();
                        if (text.Length > 0)
                            _capturedHashes.Add(text.GetHashCode(StringComparison.Ordinal));
                    }
                }
                finally
                {
                    seedDone.TrySetResult();
                }
            });
            await seedDone.Task.ConfigureAwait(false);

            // 2. Resize ConPTY to the expanded viewport.
            _process.Resize((short)cols, (short)nextDepth);

            // 3. Resize buffer on the UI thread.
            var resizeDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _dispatcher.Post(() =>
            {
                try
                {
                    Buffer.Resize(nextDepth, cols);
                }
                finally
                {
                    resizeDone.TrySetResult();
                }
            });
            await resizeDone.Task.ConfigureAwait(false);

            // 4. Wait for the TUI to redraw into the larger viewport.
            await Task.Delay(2000).ConfigureAwait(false);

            // 5. Capture only genuinely new rows and resize back.
            var captureDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _dispatcher.Post(() =>
            {
                try
                {
                    var rows = Buffer.Rows;
                    for (var r = 0; r < rows; r++)
                    {
                        var text = Buffer.GetRowText(r);
                        if (string.IsNullOrWhiteSpace(text))
                            continue;
                        var hash = text.TrimEnd().GetHashCode(StringComparison.Ordinal);
                        if (!_capturedHashes.Add(hash))
                            continue; // already on screen or pushed previously
                        Buffer.PushExternalScrollback(Buffer.GetRowCells(r));
                    }

                    // Resize buffer back to the original viewport.
                    Buffer.Resize(originalRows, cols);
                }
                finally
                {
                    captureDone.TrySetResult();
                }
            });
            await captureDone.Task.ConfigureAwait(false);

            // 6. Resize ConPTY back to the original dimensions.
            _process.Resize((short)cols, (short)originalRows);

            _capturedDepth = nextDepth;
        }
        finally
        {
            _capturingHistory = false;
        }
    }

    /// <summary>
    /// Stop the reader task, dispose the process (if owned), and raise
    /// <see cref="Exited"/>. Safe to call from any thread; idempotent.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        if (_ownsProcess)
        {
            try
            {
                _process.Dispose();
            }
            catch
            {
                // Disposing the process closes the read pipe, which the
                // reader task is blocked inside. Any exception there is
                // best-effort cleanup; do not propagate from Dispose.
            }
        }

        try
        {
            // Wait briefly for the reader to wind down. Don't block forever
            // because a misbehaving fake or stuck pipe should not hang the UI.
            _readerTask.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }

        _cts.Dispose();
        RaiseExitedOnce();
    }

    private async Task ReadLoopAsync(CancellationToken token)
    {
        var buffer = new byte[ReadBufferSize];

        try
        {
            while (!token.IsCancellationRequested)
            {
                int read;
                try
                {
                    read = await _process.OutputStream.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (IOException)
                {
                    // Pipe closed (typically: child exited and ConPTY cleared
                    // its read end). Treat as EOF.
                    return;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }

                if (read <= 0)
                {
                    return;
                }

                // Snapshot the chunk so the reader's reusable buffer can be
                // overwritten by the next ReadAsync before the dispatcher
                // gets around to running our callback.
                var chunk = new byte[read];
                System.Buffer.BlockCopy(buffer, 0, chunk, 0, read);

                try
                {
                    _dispatcher.Post(() =>
                    {
                        if (_disposed != 0)
                        {
                            return;
                        }
                        _parser.Feed(chunk);
                    });
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
            }
        }
        finally
        {
            // EOF or cancellation. Raise Exited on the dispatcher so handlers
            // observe a consistent thread.
            try
            {
                _dispatcher.Post(RaiseExitedOnce);
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    private void RaiseExitedOnce()
    {
        if (Interlocked.Exchange(ref _exitedRaised, 1) != 0)
        {
            return;
        }

        Exited?.Invoke(this, EventArgs.Empty);
    }
}
