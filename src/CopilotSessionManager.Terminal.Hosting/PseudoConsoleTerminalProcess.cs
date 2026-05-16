using System;
using System.IO;
using CopilotSessionManager.Native;

namespace CopilotSessionManager.Terminal.Hosting;

/// <summary>
/// Production <see cref="ITerminalProcess"/> backed by a
/// <see cref="PseudoConsole"/>. Spawning is delegated to
/// <see cref="PseudoConsole.Start(string, short, short, string?)"/>.
/// </summary>
public sealed class PseudoConsoleTerminalProcess : ITerminalProcess
{
    private readonly PseudoConsole _console;

    /// <summary>Adopt an already-started <see cref="PseudoConsole"/>.</summary>
    /// <remarks>
    /// Ownership transfers to this instance; disposing the process disposes
    /// the underlying console and terminates the child if it's still running.
    /// </remarks>
    public PseudoConsoleTerminalProcess(PseudoConsole console)
    {
        _console = console ?? throw new ArgumentNullException(nameof(console));
    }

    /// <summary>
    /// Spawn <paramref name="commandLine"/> attached to a fresh
    /// <see cref="PseudoConsole"/> of the given size.
    /// </summary>
    public static PseudoConsoleTerminalProcess Start(string commandLine, short cols, short rows, string? workingDirectory = null)
    {
        var console = PseudoConsole.Start(commandLine, cols, rows, workingDirectory);
        return new PseudoConsoleTerminalProcess(console);
    }

    /// <inheritdoc />
    public Stream InputStream => _console.InputStream;

    /// <inheritdoc />
    public Stream OutputStream => _console.OutputStream;

    /// <inheritdoc />
    public bool HasExited => _console.HasExited;

    /// <summary>Process id of the spawned child.</summary>
    public int ProcessId => _console.ProcessId;

    /// <inheritdoc />
    public void Resize(short cols, short rows) => _console.Resize(cols, rows);

    /// <inheritdoc />
    public void Dispose() => _console.Dispose();
}
