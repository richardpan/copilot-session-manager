using System;

namespace CopilotSessionManager.Terminal;

/// <summary>
/// Modifier keys held during a <see cref="TerminalKey"/> press. The numeric
/// values are arranged so that <c>1 + (int)modifiers</c> produces the
/// xterm modifier parameter used in CSI sequences (Shift=2, Alt=3,
/// Shift+Alt=4, Ctrl=5, Shift+Ctrl=6, Alt+Ctrl=7, Shift+Alt+Ctrl=8).
/// </summary>
[Flags]
public enum TerminalKeyModifiers
{
    /// <summary>No modifiers held.</summary>
    None = 0,

    /// <summary>Shift held.</summary>
    Shift = 1,

    /// <summary>Alt (Meta) held.</summary>
    Alt = 2,

    /// <summary>Control held.</summary>
    Control = 4,
}
