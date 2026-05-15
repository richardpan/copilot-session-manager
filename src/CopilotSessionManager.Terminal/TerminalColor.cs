using System;

namespace CopilotSessionManager.Terminal;

/// <summary>
/// Terminal-cell colour. A small struct with three forms: the terminal's
/// own default, an indexed palette colour (0-255 — the standard 16 ANSI
/// colours plus the xterm 256-colour cube), or a 24-bit RGB true colour.
/// </summary>
public readonly struct TerminalColor : IEquatable<TerminalColor>
{
    private readonly int _value;

    /// <summary>The form this colour takes.</summary>
    public TerminalColorKind Kind { get; }

    private TerminalColor(TerminalColorKind kind, int value)
    {
        Kind = kind;
        _value = value;
    }

    /// <summary>The "use the terminal's default" colour.</summary>
    public static TerminalColor Default { get; } = new(TerminalColorKind.Default, 0);

    /// <summary>An indexed palette colour (0-255).</summary>
    public static TerminalColor Indexed(int index)
    {
        if (index < 0 || index > 255)
        {
            throw new ArgumentOutOfRangeException(nameof(index), "Indexed colour must be in 0-255.");
        }
        return new(TerminalColorKind.Indexed, index);
    }

    /// <summary>A 24-bit RGB true colour.</summary>
    public static TerminalColor Rgb(byte red, byte green, byte blue)
        => new(TerminalColorKind.Rgb, (red << 16) | (green << 8) | blue);

    /// <summary>Palette index when <see cref="Kind"/> is <see cref="TerminalColorKind.Indexed"/>.</summary>
    public int Index => Kind == TerminalColorKind.Indexed ? _value
        : throw new InvalidOperationException("Index is only valid for Indexed colours.");

    /// <summary>Red channel when <see cref="Kind"/> is <see cref="TerminalColorKind.Rgb"/>.</summary>
    public byte Red => Kind == TerminalColorKind.Rgb ? (byte)((_value >> 16) & 0xFF)
        : throw new InvalidOperationException("Red is only valid for Rgb colours.");

    /// <summary>Green channel when <see cref="Kind"/> is <see cref="TerminalColorKind.Rgb"/>.</summary>
    public byte Green => Kind == TerminalColorKind.Rgb ? (byte)((_value >> 8) & 0xFF)
        : throw new InvalidOperationException("Green is only valid for Rgb colours.");

    /// <summary>Blue channel when <see cref="Kind"/> is <see cref="TerminalColorKind.Rgb"/>.</summary>
    public byte Blue => Kind == TerminalColorKind.Rgb ? (byte)(_value & 0xFF)
        : throw new InvalidOperationException("Blue is only valid for Rgb colours.");

    public bool Equals(TerminalColor other) => Kind == other.Kind && _value == other._value;
    public override bool Equals(object? obj) => obj is TerminalColor c && Equals(c);
    public override int GetHashCode() => HashCode.Combine((int)Kind, _value);
    public static bool operator ==(TerminalColor left, TerminalColor right) => left.Equals(right);
    public static bool operator !=(TerminalColor left, TerminalColor right) => !left.Equals(right);

    public override string ToString() => Kind switch
    {
        TerminalColorKind.Default => "Default",
        TerminalColorKind.Indexed => $"Indexed({_value})",
        TerminalColorKind.Rgb => $"Rgb({Red},{Green},{Blue})",
        _ => "?",
    };
}

/// <summary>Discriminator for the three forms a <see cref="TerminalColor"/> can take.</summary>
public enum TerminalColorKind : byte
{
    /// <summary>Use whatever default colour the terminal renderer chooses.</summary>
    Default = 0,
    /// <summary>Indexed palette colour, 0-255.</summary>
    Indexed = 1,
    /// <summary>24-bit RGB true colour.</summary>
    Rgb = 2,
}
