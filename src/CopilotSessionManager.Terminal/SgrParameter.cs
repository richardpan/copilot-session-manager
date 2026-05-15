namespace CopilotSessionManager.Terminal;

/// <summary>
/// One typed parameter from a CSI <c>... m</c> Select Graphic Rendition
/// sequence. The set covers the attributes Copilot CLI emits in practice;
/// extension is straightforward.
/// </summary>
public abstract record SgrParameter;

/// <summary>SGR 0 — reset all attributes to defaults.</summary>
public sealed record SgrReset : SgrParameter;

/// <summary>SGR 1 (on) / SGR 22 (off) — bold weight.</summary>
public sealed record SgrBold(bool On) : SgrParameter;

/// <summary>SGR 2 (on) / SGR 22 (off) — dim / faint weight.</summary>
public sealed record SgrDim(bool On) : SgrParameter;

/// <summary>SGR 3 (on) / SGR 23 (off) — italic.</summary>
public sealed record SgrItalic(bool On) : SgrParameter;

/// <summary>SGR 4 (on) / SGR 24 (off) — underline.</summary>
public sealed record SgrUnderline(bool On) : SgrParameter;

/// <summary>SGR 7 (on) / SGR 27 (off) — reverse video.</summary>
public sealed record SgrInverse(bool On) : SgrParameter;

/// <summary>SGR 9 (on) / SGR 29 (off) — strikethrough.</summary>
public sealed record SgrStrikethrough(bool On) : SgrParameter;

/// <summary>
/// SGR 30-37 (basic) and 90-97 (bright) — set foreground to one of the
/// 16 ANSI palette colours. <see cref="Index"/> is 0-15.
/// </summary>
public sealed record SgrForegroundIndex(int Index) : SgrParameter;

/// <summary>
/// SGR 40-47 (basic) and 100-107 (bright) — set background to one of the
/// 16 ANSI palette colours. <see cref="Index"/> is 0-15.
/// </summary>
public sealed record SgrBackgroundIndex(int Index) : SgrParameter;

/// <summary>SGR 39 — reset foreground to default.</summary>
public sealed record SgrForegroundDefault : SgrParameter;

/// <summary>SGR 49 — reset background to default.</summary>
public sealed record SgrBackgroundDefault : SgrParameter;

/// <summary>SGR 38;5;n — 256-colour palette foreground.</summary>
public sealed record SgrForeground256(int Index) : SgrParameter;

/// <summary>SGR 48;5;n — 256-colour palette background.</summary>
public sealed record SgrBackground256(int Index) : SgrParameter;

/// <summary>SGR 38;2;r;g;b — true-colour foreground.</summary>
public sealed record SgrForegroundRgb(byte R, byte G, byte B) : SgrParameter;

/// <summary>SGR 48;2;r;g;b — true-colour background.</summary>
public sealed record SgrBackgroundRgb(byte R, byte G, byte B) : SgrParameter;

/// <summary>
/// Diagnostic for parameters the parser does not recognise. The raw
/// numeric value is preserved so the vocabulary catalogue can flag gaps.
/// </summary>
public sealed record SgrUnknown(int Value) : SgrParameter;
