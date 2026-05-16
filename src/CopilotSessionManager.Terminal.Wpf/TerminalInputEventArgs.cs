using System;

namespace CopilotSessionManager.Terminal.Wpf;

/// <summary>
/// Event payload raised by <see cref="TerminalControl"/> when keyboard
/// input, text input, or a paste operation has produced bytes destined
/// for the PTY input stream.
/// </summary>
public sealed class TerminalInputEventArgs : EventArgs
{
    /// <summary>Construct a new event payload around the produced bytes.</summary>
    public TerminalInputEventArgs(ReadOnlyMemory<byte> bytes)
    {
        Bytes = bytes;
    }

    /// <summary>The UTF-8 / VT bytes ready to be written to the PTY.</summary>
    public ReadOnlyMemory<byte> Bytes { get; }
}
