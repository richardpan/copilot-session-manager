using System.Collections.Generic;
using System.Text;

namespace CopilotSessionManager.Terminal;

/// <summary>
/// Discriminated union of events produced by <see cref="VtParser"/> as it
/// consumes a byte stream from a pseudo-console. Phase 2A of epic #93.
/// </summary>
/// <remarks>
/// The set is deliberately small and Copilot-CLI-shaped: only the escape
/// sequences we observe in the wild get a typed event. Everything else
/// surfaces as <see cref="UnknownSequence"/> for diagnostics. As real
/// traces are captured (Phase 2C) the vocabulary is expanded.
/// </remarks>
public abstract record VtEvent;

/// <summary>A single printable Unicode code point arriving at the cursor.</summary>
public sealed record PrintRune(Rune Glyph) : VtEvent;

/// <summary>LF (0x0A) — move cursor to the next line.</summary>
public sealed record LineFeed : VtEvent;

/// <summary>CR (0x0D) — move cursor to column 1.</summary>
public sealed record CarriageReturn : VtEvent;

/// <summary>BS (0x08) — move cursor one column left.</summary>
public sealed record Backspace : VtEvent;

/// <summary>HT (0x09) — advance cursor to the next tab stop.</summary>
public sealed record HorizontalTab : VtEvent;

/// <summary>BEL (0x07) — audible / visible bell.</summary>
public sealed record RingBell : VtEvent;

/// <summary>
/// CSI <c>row;col H</c> (or <c>f</c>) — set the cursor to an absolute
/// 1-based <paramref name="Row"/> and <paramref name="Column"/>. Default
/// for missing parameters is 1.
/// </summary>
public sealed record SetCursorPosition(int Row, int Column) : VtEvent;

/// <summary>CSI <c>n A</c> — move cursor up <paramref name="Lines"/> lines.</summary>
public sealed record MoveCursorUp(int Lines) : VtEvent;

/// <summary>CSI <c>n B</c> — move cursor down <paramref name="Lines"/> lines.</summary>
public sealed record MoveCursorDown(int Lines) : VtEvent;

/// <summary>CSI <c>n C</c> — move cursor right <paramref name="Columns"/> columns.</summary>
public sealed record MoveCursorForward(int Columns) : VtEvent;

/// <summary>CSI <c>n D</c> — move cursor left <paramref name="Columns"/> columns.</summary>
public sealed record MoveCursorBack(int Columns) : VtEvent;

/// <summary>ESC 7 / CSI s — save cursor position and SGR state.</summary>
public sealed record SaveCursor : VtEvent;

/// <summary>ESC 8 / CSI u — restore cursor position and SGR state.</summary>
public sealed record RestoreCursor : VtEvent;

/// <summary>CSI <c>n J</c> — erase region of the display.</summary>
public sealed record EraseInDisplay(EraseMode Mode) : VtEvent;

/// <summary>CSI <c>n K</c> — erase region of the current line.</summary>
public sealed record EraseInLine(EraseMode Mode) : VtEvent;

/// <summary>
/// CSI <c>... m</c> — Select Graphic Rendition. Multiple parameters may
/// arrive in a single sequence; <see cref="Parameters"/> preserves order
/// because some combinations (e.g. <c>38;5;n</c>) span multiple values.
/// </summary>
public sealed record SetGraphicsRendition(IReadOnlyList<SgrParameter> Parameters) : VtEvent;

/// <summary>CSI <c>?25 h</c> / <c>?25 l</c> — DECTCEM cursor visibility.</summary>
public sealed record SetCursorVisibility(bool Visible) : VtEvent;

/// <summary>
/// CSI <c>?1049 h</c> / <c>?1049 l</c> — switch to / from the alternate
/// screen buffer.
/// </summary>
public sealed record SetUseAlternateScreen(bool Use) : VtEvent;

/// <summary>CSI <c>?2004 h</c> / <c>?2004 l</c> — bracketed paste mode.</summary>
public sealed record SetBracketedPaste(bool Enabled) : VtEvent;

/// <summary>
/// Catch-all for mode-set / mode-reset sequences whose number we do not
/// have a typed event for. Consumers can ignore these or log them.
/// </summary>
public sealed record SetMode(int Mode, bool DecPrivate, bool Enabled) : VtEvent;

/// <summary>CSI <c>n S</c> — scroll the display up by <paramref name="Lines"/> lines.</summary>
public sealed record ScrollUp(int Lines) : VtEvent;

/// <summary>CSI <c>n T</c> — scroll the display down by <paramref name="Lines"/> lines.</summary>
public sealed record ScrollDown(int Lines) : VtEvent;

/// <summary>OSC <c>0</c> / OSC <c>2</c> — set window / icon title.</summary>
public sealed record SetWindowTitle(string Title) : VtEvent;

/// <summary>ESC <c>c</c> (RIS) — full terminal reset.</summary>
public sealed record ResetTerminal : VtEvent;

/// <summary>
/// Diagnostic event surfaced when the parser sees a syntactically valid
/// but semantically unrecognised escape sequence. The <paramref name="RawBytes"/>
/// are preserved so that traces can be replayed back, and so that the
/// vocabulary catalogue (Phase 2C) can flag gaps.
/// </summary>
public sealed record UnknownSequence(string Description, IReadOnlyList<byte> RawBytes) : VtEvent;
