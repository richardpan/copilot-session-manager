namespace CopilotSessionManager.Terminal;

/// <summary>
/// Region selector used by both <see cref="EraseInDisplay"/> and
/// <see cref="EraseInLine"/>. Numeric values mirror the CSI parameter
/// (so 0 = ToEnd, 1 = ToStart, 2 = All, 3 = Scrollback).
/// </summary>
public enum EraseMode
{
    /// <summary>Erase from the cursor (inclusive) to the end of the region.</summary>
    ToEnd = 0,

    /// <summary>Erase from the start of the region to the cursor (inclusive).</summary>
    ToStart = 1,

    /// <summary>Erase the entire region.</summary>
    All = 2,

    /// <summary>Erase the scroll-back buffer (display only — xterm extension).</summary>
    Scrollback = 3,
}
