using System;

namespace CopilotSessionManager.Terminal;

/// <summary>
/// Streams bytes through a VT escape-sequence parser and produces a
/// sequence of typed <see cref="VtEvent"/> values. Implementations must
/// retain state across <see cref="Feed"/> calls so that escape sequences
/// split across read boundaries (a common ConPTY occurrence) parse
/// correctly.
/// </summary>
public interface IVtParser
{
    /// <summary>
    /// Consume <paramref name="bytes"/> and dispatch any completed events
    /// to the sink supplied at construction.
    /// </summary>
    void Feed(ReadOnlySpan<byte> bytes);

    /// <summary>
    /// Force the parser back to the GROUND state, discarding any in-flight
    /// sequence. Useful when reattaching to a different child stream.
    /// </summary>
    void Reset();
}
