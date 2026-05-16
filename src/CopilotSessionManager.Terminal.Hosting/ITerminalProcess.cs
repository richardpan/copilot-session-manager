using System;
using System.IO;

namespace CopilotSessionManager.Terminal.Hosting;

/// <summary>
/// Process-side abstraction that <see cref="TerminalSession"/> reads from
/// and writes to. Real sessions use <see cref="PseudoConsoleTerminalProcess"/>,
/// which wraps a <see cref="CopilotSessionManager.Native.PseudoConsole"/>;
/// tests inject in-memory stream pairs so the parser/buffer pipeline can
/// be exercised without spawning a child process.
/// </summary>
public interface ITerminalProcess : IDisposable
{
    /// <summary>Write-side stream into the child's stdin.</summary>
    Stream InputStream { get; }

    /// <summary>Read-side stream of the child's stdout (interleaved by ConPTY with VT escapes).</summary>
    Stream OutputStream { get; }

    /// <summary>True once the child has exited (or, for fakes, the output stream has reached EOF).</summary>
    bool HasExited { get; }

    /// <summary>Resize the underlying pseudo-console / fake.</summary>
    void Resize(short cols, short rows);
}
