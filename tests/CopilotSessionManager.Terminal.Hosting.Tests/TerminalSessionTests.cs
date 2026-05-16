using System;
using System.Text;
using System.Threading;
using CopilotSessionManager.Terminal;
using CopilotSessionManager.Terminal.Hosting;
using FluentAssertions;

namespace CopilotSessionManager.Terminal.Hosting.Tests;

public class TerminalSessionTests
{
    [Fact]
    public void Constructor_creates_buffer_with_requested_dimensions()
    {
        using var process = new FakeTerminalProcess();
        var dispatcher = new QueuedDispatcher();

        using var session = new TerminalSession(process, dispatcher, rows: 24, cols: 80);

        session.Buffer.Rows.Should().Be(24);
        session.Buffer.Columns.Should().Be(80);
    }

    [Theory]
    [InlineData(0, 80)]
    [InlineData(24, 0)]
    [InlineData(-1, 80)]
    [InlineData(24, -1)]
    public void Constructor_rejects_non_positive_dimensions(int rows, int cols)
    {
        using var process = new FakeTerminalProcess();
        var dispatcher = new QueuedDispatcher();

        var act = () => new TerminalSession(process, dispatcher, rows, cols);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Constructor_rejects_null_process_or_dispatcher()
    {
        using var process = new FakeTerminalProcess();
        var dispatcher = new QueuedDispatcher();

        var nullProcess = () => new TerminalSession(null!, dispatcher, 24, 80);
        var nullDispatcher = () => new TerminalSession(process, null!, 24, 80);

        nullProcess.Should().Throw<ArgumentNullException>();
        nullDispatcher.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Bytes_emitted_by_process_flow_into_screen_buffer()
    {
        using var process = new FakeTerminalProcess();
        var dispatcher = new QueuedDispatcher();
        using var session = new TerminalSession(process, dispatcher, rows: 5, cols: 20);

        process.EmitOutput(Encoding.ASCII.GetBytes("hello"));

        // Pump until the dispatcher has executed the parser callback.
        dispatcher.Pump(timeoutMs: 2000, stop: () => session.Buffer.GetCell(1, 1).Glyph.Value == 'h');

        var rendered = new StringBuilder();
        for (int c = 1; c <= 5; c++)
        {
            rendered.Append((char)session.Buffer.GetCell(1, c).Glyph.Value);
        }
        rendered.ToString().Should().Be("hello");
    }

    [Fact]
    public void SendInput_writes_bytes_to_process_input_stream()
    {
        using var process = new FakeTerminalProcess();
        var dispatcher = new QueuedDispatcher();
        using var session = new TerminalSession(process, dispatcher, rows: 5, cols: 20);

        session.SendInput(new byte[] { (byte)'a', (byte)'b', 0x0D });

        process.DrainInput().Should().Equal((byte)'a', (byte)'b', 0x0D);
    }

    [Fact]
    public void SendInput_with_empty_span_is_noop()
    {
        using var process = new FakeTerminalProcess();
        var dispatcher = new QueuedDispatcher();
        using var session = new TerminalSession(process, dispatcher, rows: 5, cols: 20);

        session.SendInput(ReadOnlySpan<byte>.Empty);

        process.DrainInput(timeoutMs: 100).Should().BeEmpty();
    }

    [Fact]
    public void SendInput_after_dispose_throws()
    {
        using var process = new FakeTerminalProcess();
        var dispatcher = new QueuedDispatcher();
        var session = new TerminalSession(process, dispatcher, rows: 5, cols: 20);
        session.Dispose();

        var act = () => session.SendInput(new byte[] { 1 });

        act.Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public void Resize_calls_process_resize_and_resizes_buffer()
    {
        using var process = new FakeTerminalProcess();
        var dispatcher = new QueuedDispatcher();
        using var session = new TerminalSession(process, dispatcher, rows: 24, cols: 80);

        session.Resize(rows: 30, cols: 100);
        // Buffer resize is dispatched; pump to flush it.
        dispatcher.Pump(timeoutMs: 500, stop: () => session.Buffer.Rows == 30);

        process.LastResize.Should().Be((cols: (short)100, rows: (short)30));
        session.Buffer.Rows.Should().Be(30);
        session.Buffer.Columns.Should().Be(100);
    }

    [Theory]
    [InlineData(0, 80)]
    [InlineData(24, 0)]
    [InlineData(-1, 80)]
    public void Resize_rejects_non_positive_dimensions(int rows, int cols)
    {
        using var process = new FakeTerminalProcess();
        var dispatcher = new QueuedDispatcher();
        using var session = new TerminalSession(process, dispatcher, 24, 80);

        var act = () => session.Resize(rows, cols);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Exited_event_fires_when_process_emits_eof()
    {
        using var process = new FakeTerminalProcess();
        var dispatcher = new QueuedDispatcher();
        using var session = new TerminalSession(process, dispatcher, rows: 5, cols: 20);

        var exitedFired = 0;
        session.Exited += (_, _) => Interlocked.Increment(ref exitedFired);

        process.SignalExit();

        // Reader hits EOF on a background thread; it then posts RaiseExitedOnce
        // via the dispatcher. Pump until we see it.
        dispatcher.Pump(timeoutMs: 2000, stop: () => Volatile.Read(ref exitedFired) >= 1);

        exitedFired.Should().Be(1);
    }

    [Fact]
    public void Exited_event_fires_at_most_once()
    {
        using var process = new FakeTerminalProcess();
        var dispatcher = new QueuedDispatcher();
        var session = new TerminalSession(process, dispatcher, rows: 5, cols: 20);

        var exitedFired = 0;
        session.Exited += (_, _) => Interlocked.Increment(ref exitedFired);

        process.SignalExit();
        dispatcher.Pump(timeoutMs: 2000, stop: () => Volatile.Read(ref exitedFired) >= 1);

        // Disposing afterwards must not raise Exited a second time. Dispose
        // raises synchronously; the reader's posted callback may also run.
        session.Dispose();
        dispatcher.Pump(timeoutMs: 200);

        exitedFired.Should().Be(1);
    }

    [Fact]
    public void Dispose_owns_process_by_default()
    {
        var process = new FakeTerminalProcess();
        var dispatcher = new QueuedDispatcher();
        var session = new TerminalSession(process, dispatcher, rows: 5, cols: 20);

        session.Dispose();

        process.DisposeCount.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Dispose_skips_process_when_not_owned()
    {
        using var process = new FakeTerminalProcess();
        var dispatcher = new QueuedDispatcher();
        var session = new TerminalSession(process, dispatcher, rows: 5, cols: 20, ownsProcess: false);

        session.Dispose();

        process.DisposeCount.Should().Be(0);
    }

    [Fact]
    public void Dispose_is_idempotent()
    {
        using var process = new FakeTerminalProcess();
        var dispatcher = new QueuedDispatcher();
        var session = new TerminalSession(process, dispatcher, rows: 5, cols: 20);

        session.Dispose();
        var act = () => session.Dispose();

        act.Should().NotThrow();
    }

    [Fact]
    public void Reader_survives_escape_sequence_split_across_reads()
    {
        using var process = new FakeTerminalProcess();
        var dispatcher = new QueuedDispatcher();
        using var session = new TerminalSession(process, dispatcher, rows: 5, cols: 20);

        // CSI cursor-position "ESC [ 2 ; 3 H" then 'X'. Split right in the
        // middle of the CSI parameters so the parser must keep state across
        // two Feed calls.
        process.EmitOutput(new byte[] { 0x1B, (byte)'[', (byte)'2' });
        dispatcher.Pump(timeoutMs: 500);
        process.EmitOutput(new byte[] { (byte)';', (byte)'3', (byte)'H', (byte)'X' });

        dispatcher.Pump(timeoutMs: 2000, stop: () => session.Buffer.GetCell(2, 3).Glyph.Value == 'X');

        session.Buffer.GetCell(2, 3).Glyph.Value.Should().Be('X');
    }
}
