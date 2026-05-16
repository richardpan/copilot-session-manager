namespace CopilotSessionManager.Terminal;

/// <summary>
/// Logical terminal keys recognised by <see cref="VtKeyEncoder"/>. Each
/// value maps to a distinct VT/xterm byte sequence; printable characters
/// are deliberately not modelled here — those flow through text input
/// instead.
/// </summary>
public enum TerminalKey
{
    /// <summary>Sentinel value meaning "no encodable key".</summary>
    None,

    /// <summary>Cursor up — <c>ESC [ A</c> (normal) / <c>ESC O A</c> (DECCKM).</summary>
    Up,

    /// <summary>Cursor down — <c>ESC [ B</c> (normal) / <c>ESC O B</c> (DECCKM).</summary>
    Down,

    /// <summary>Cursor right — <c>ESC [ C</c> (normal) / <c>ESC O C</c> (DECCKM).</summary>
    Right,

    /// <summary>Cursor left — <c>ESC [ D</c> (normal) / <c>ESC O D</c> (DECCKM).</summary>
    Left,

    /// <summary>Home key — <c>ESC [ H</c> (normal) / <c>ESC O H</c> (DECCKM).</summary>
    Home,

    /// <summary>End key — <c>ESC [ F</c> (normal) / <c>ESC O F</c> (DECCKM).</summary>
    End,

    /// <summary>Page Up — <c>ESC [ 5 ~</c>.</summary>
    PageUp,

    /// <summary>Page Down — <c>ESC [ 6 ~</c>.</summary>
    PageDown,

    /// <summary>Insert — <c>ESC [ 2 ~</c>.</summary>
    Insert,

    /// <summary>Delete (forward) — <c>ESC [ 3 ~</c>.</summary>
    Delete,

    /// <summary>F1 — <c>ESC O P</c>.</summary>
    F1,

    /// <summary>F2 — <c>ESC O Q</c>.</summary>
    F2,

    /// <summary>F3 — <c>ESC O R</c>.</summary>
    F3,

    /// <summary>F4 — <c>ESC O S</c>.</summary>
    F4,

    /// <summary>F5 — <c>ESC [ 15 ~</c>.</summary>
    F5,

    /// <summary>F6 — <c>ESC [ 17 ~</c>.</summary>
    F6,

    /// <summary>F7 — <c>ESC [ 18 ~</c>.</summary>
    F7,

    /// <summary>F8 — <c>ESC [ 19 ~</c>.</summary>
    F8,

    /// <summary>F9 — <c>ESC [ 20 ~</c>.</summary>
    F9,

    /// <summary>F10 — <c>ESC [ 21 ~</c>.</summary>
    F10,

    /// <summary>F11 — <c>ESC [ 23 ~</c>.</summary>
    F11,

    /// <summary>F12 — <c>ESC [ 24 ~</c>.</summary>
    F12,

    /// <summary>Tab — <c>0x09</c>. Shift+Tab is encoded as <c>ESC [ Z</c>.</summary>
    Tab,

    /// <summary>Enter / Return — <c>0x0D</c>.</summary>
    Enter,

    /// <summary>Backspace — <c>0x7F</c> (DEL, matching modern xterm/Unix conventions).</summary>
    Backspace,

    /// <summary>Escape — <c>0x1B</c>.</summary>
    Escape,
}
