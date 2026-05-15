using System;
using System.Text;

namespace CopilotSessionManager.Terminal;

/// <summary>
/// Style attributes that can decorate a terminal cell. Modelled as flags
/// so a single byte encodes the full combination.
/// </summary>
[Flags]
public enum CellAttributes : byte
{
    /// <summary>Default rendition.</summary>
    None = 0,
    /// <summary>Bold weight (SGR 1).</summary>
    Bold = 1 << 0,
    /// <summary>Dim / faint weight (SGR 2).</summary>
    Dim = 1 << 1,
    /// <summary>Italic (SGR 3).</summary>
    Italic = 1 << 2,
    /// <summary>Underline (SGR 4).</summary>
    Underline = 1 << 3,
    /// <summary>Reverse video (SGR 7).</summary>
    Inverse = 1 << 4,
    /// <summary>Strikethrough (SGR 9).</summary>
    Strikethrough = 1 << 5,
}

/// <summary>
/// One cell of the terminal grid. Cheap value type so we can store a
/// dense grid plus an alternate buffer plus scroll-back without
/// generating per-cell allocations.
/// </summary>
public readonly struct TerminalCell : IEquatable<TerminalCell>
{
    /// <summary>The visible glyph; defaults to U+0020 (space).</summary>
    public Rune Glyph { get; }

    /// <summary>Foreground colour for this cell.</summary>
    public TerminalColor Foreground { get; }

    /// <summary>Background colour for this cell.</summary>
    public TerminalColor Background { get; }

    /// <summary>Style flags (bold, italic, underline, ...).</summary>
    public CellAttributes Attributes { get; }

    public TerminalCell(Rune glyph, TerminalColor foreground, TerminalColor background, CellAttributes attributes)
    {
        Glyph = glyph;
        Foreground = foreground;
        Background = background;
        Attributes = attributes;
    }

    /// <summary>An empty cell using terminal defaults.</summary>
    public static TerminalCell Empty { get; } =
        new(new Rune(' '), TerminalColor.Default, TerminalColor.Default, CellAttributes.None);

    public bool Equals(TerminalCell other) =>
        Glyph.Value == other.Glyph.Value
        && Foreground == other.Foreground
        && Background == other.Background
        && Attributes == other.Attributes;

    public override bool Equals(object? obj) => obj is TerminalCell c && Equals(c);

    public override int GetHashCode() =>
        HashCode.Combine(Glyph.Value, Foreground, Background, (byte)Attributes);

    public static bool operator ==(TerminalCell left, TerminalCell right) => left.Equals(right);
    public static bool operator !=(TerminalCell left, TerminalCell right) => !left.Equals(right);

    public override string ToString() =>
        $"'{Glyph}' fg={Foreground} bg={Background} attrs={Attributes}";
}
