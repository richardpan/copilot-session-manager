using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using CopilotSessionManager.Terminal.Hosting;

namespace CopilotSessionManager.Terminal.Hosting.Tests;

/// <summary>
/// In-memory <see cref="ITerminalProcess"/> backed by two anonymous pipes
/// (one for stdin into the "child", one for stdout out of it). Lets tests
/// push bytes into the parser / read bytes the session emitted without
/// spawning a real process.
/// </summary>
internal sealed class FakeTerminalProcess : ITerminalProcess
{
    private readonly AnonymousPipeServerStream _stdoutWriteSide;
    private readonly AnonymousPipeClientStream _stdoutReadSide;
    private readonly AnonymousPipeServerStream _stdinReadSide;
    private readonly AnonymousPipeClientStream _stdinWriteSide;

    public FakeTerminalProcess()
    {
        _stdoutWriteSide = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.None);
        _stdoutReadSide = new AnonymousPipeClientStream(PipeDirection.In, _stdoutWriteSide.ClientSafePipeHandle);

        _stdinReadSide = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.None);
        _stdinWriteSide = new AnonymousPipeClientStream(PipeDirection.Out, _stdinReadSide.ClientSafePipeHandle);
    }

    public Stream InputStream => _stdinWriteSide;
    public Stream OutputStream => _stdoutReadSide;
    public bool HasExited { get; private set; }

    public (short Cols, short Rows)? LastResize { get; private set; }
    public int DisposeCount { get; private set; }

    public void Resize(short cols, short rows) => LastResize = (cols, rows);

    /// <summary>Write bytes that the "child" wants the host to see.</summary>
    public void EmitOutput(byte[] bytes)
    {
        _stdoutWriteSide.Write(bytes);
        _stdoutWriteSide.Flush();
    }

    /// <summary>Read everything the host wrote into stdin so far (non-blocking).</summary>
    public byte[] DrainInput(int timeoutMs = 500)
    {
        var deadline = Environment.TickCount + timeoutMs;
        using var ms = new MemoryStream();
        var buf = new byte[1024];
        while (Environment.TickCount < deadline)
        {
            // Anonymous pipes don't expose DataAvailable; use a short async read with timeout.
            using var cts = new CancellationTokenSource(50);
            try
            {
                var read = _stdinReadSide.ReadAsync(buf.AsMemory(), cts.Token).AsTask().GetAwaiter().GetResult();
                if (read <= 0)
                {
                    break;
                }
                ms.Write(buf, 0, read);
                return ms.ToArray();
            }
            catch (OperationCanceledException)
            {
                if (ms.Length > 0)
                {
                    return ms.ToArray();
                }
            }
        }
        return ms.ToArray();
    }

    /// <summary>Mark the child as exited and close its stdout, signalling EOF.</summary>
    public void SignalExit()
    {
        HasExited = true;
        _stdoutWriteSide.Dispose();
    }

    public void Dispose()
    {
        DisposeCount++;
        HasExited = true;
        try
        { _stdoutWriteSide.Dispose(); }
        catch { }
        try
        { _stdoutReadSide.Dispose(); }
        catch { }
        try
        { _stdinReadSide.Dispose(); }
        catch { }
        try
        { _stdinWriteSide.Dispose(); }
        catch { }
    }
}
