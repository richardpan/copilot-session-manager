using System;

namespace CopilotSessionManager.Terminal;

/// <summary>
/// Mutable "pen" — the current SGR state that gets baked into newly
/// printed cells. Kept separate from <see cref="TerminalCell"/> so that
/// styling updates don't allocate new cells per-call.
/// </summary>
public sealed class TerminalStyle
{
    /// <summary>Current foreground colour (default until SGR overrides it).</summary>
    public TerminalColor Foreground { get; private set; } = TerminalColor.Default;

    /// <summary>Current background colour (default until SGR overrides it).</summary>
    public TerminalColor Background { get; private set; } = TerminalColor.Default;

    /// <summary>Bitmask of currently active style attributes.</summary>
    public CellAttributes Attributes { get; private set; } = CellAttributes.None;

    /// <summary>Reset everything to defaults (SGR 0 semantics).</summary>
    public void Reset()
    {
        Foreground = TerminalColor.Default;
        Background = TerminalColor.Default;
        Attributes = CellAttributes.None;
    }

    /// <summary>Mint a cell with the supplied glyph and the current pen state.</summary>
    public TerminalCell Stamp(System.Text.Rune glyph) =>
        new(glyph, Foreground, Background, Attributes);

    /// <summary>
    /// Apply one parsed SGR parameter to the pen.
    /// Unknown parameters are ignored — the parser surfaces them via
    /// <see cref="SgrUnknown"/> so they can be triaged separately.
    /// </summary>
    public void Apply(SgrParameter parameter)
    {
        switch (parameter)
        {
            case SgrReset:
                Reset();
                break;
            case SgrBold(var on):
                SetFlag(CellAttributes.Bold, on);
                break;
            case SgrDim(var on):
                SetFlag(CellAttributes.Dim, on);
                break;
            case SgrItalic(var on):
                SetFlag(CellAttributes.Italic, on);
                break;
            case SgrUnderline(var on):
                SetFlag(CellAttributes.Underline, on);
                break;
            case SgrInverse(var on):
                SetFlag(CellAttributes.Inverse, on);
                break;
            case SgrStrikethrough(var on):
                SetFlag(CellAttributes.Strikethrough, on);
                break;

            case SgrForegroundIndex(var i):
                Foreground = TerminalColor.Indexed(i);
                break;
            case SgrBackgroundIndex(var i):
                Background = TerminalColor.Indexed(i);
                break;
            case SgrForegroundDefault:
                Foreground = TerminalColor.Default;
                break;
            case SgrBackgroundDefault:
                Background = TerminalColor.Default;
                break;
            case SgrForeground256(var i):
                Foreground = TerminalColor.Indexed(i);
                break;
            case SgrBackground256(var i):
                Background = TerminalColor.Indexed(i);
                break;
            case SgrForegroundRgb(var r, var g, var b):
                Foreground = TerminalColor.Rgb(r, g, b);
                break;
            case SgrBackgroundRgb(var r, var g, var b):
                Background = TerminalColor.Rgb(r, g, b);
                break;

            case SgrUnknown:
                break;
        }
    }

    private void SetFlag(CellAttributes flag, bool on)
    {
        if (on)
            Attributes |= flag;
        else
            Attributes &= ~flag;
    }

    /// <summary>Capture a snapshot of the current pen for save/restore.</summary>
    internal TerminalStyleSnapshot Snapshot() =>
        new(Foreground, Background, Attributes);

    /// <summary>Restore from a previously captured snapshot.</summary>
    internal void Restore(TerminalStyleSnapshot snapshot)
    {
        Foreground = snapshot.Foreground;
        Background = snapshot.Background;
        Attributes = snapshot.Attributes;
    }
}

internal readonly record struct TerminalStyleSnapshot(
    TerminalColor Foreground, TerminalColor Background, CellAttributes Attributes);
