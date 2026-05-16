using System;
using System.Windows.Media;
using CopilotSessionManager.Terminal;

namespace CopilotSessionManager.Terminal.Wpf;

/// <summary>
/// Resolves a <see cref="TerminalColor"/> against a default foreground /
/// background pair into a WPF <see cref="Color"/>.
/// </summary>
/// <remarks>
/// The palette is the xterm 16-colour default for indices 0-15, followed
/// by the standard xterm 6 × 6 × 6 colour cube for 16-231, and the 24-step
/// grayscale ramp for 232-255. Brightness and contrast match the values
/// produced by mainstream terminal emulators.
/// </remarks>
public static class TerminalPalette
{
    private static readonly Color[] _table = BuildTable();

    /// <summary>Look up the indexed palette colour, 0-255.</summary>
    public static Color Indexed(int index)
    {
        if ((uint)index > 255)
        {
            throw new ArgumentOutOfRangeException(nameof(index), "Indexed colour must be in 0-255.");
        }
        return _table[index];
    }

    /// <summary>
    /// Resolve a <see cref="TerminalColor"/> to a WPF <see cref="Color"/>,
    /// using <paramref name="defaultColor"/> when the input is
    /// <see cref="TerminalColor.Default"/>.
    /// </summary>
    public static Color Resolve(TerminalColor color, Color defaultColor) => color.Kind switch
    {
        TerminalColorKind.Default => defaultColor,
        TerminalColorKind.Indexed => Indexed(color.Index),
        TerminalColorKind.Rgb => Color.FromRgb(color.Red, color.Green, color.Blue),
        _ => defaultColor,
    };

    private static Color[] BuildTable()
    {
        var t = new Color[256];

        t[0] = Color.FromRgb(0x00, 0x00, 0x00);
        t[1] = Color.FromRgb(0xCD, 0x00, 0x00);
        t[2] = Color.FromRgb(0x00, 0xCD, 0x00);
        t[3] = Color.FromRgb(0xCD, 0xCD, 0x00);
        t[4] = Color.FromRgb(0x00, 0x00, 0xEE);
        t[5] = Color.FromRgb(0xCD, 0x00, 0xCD);
        t[6] = Color.FromRgb(0x00, 0xCD, 0xCD);
        t[7] = Color.FromRgb(0xE5, 0xE5, 0xE5);

        t[8] = Color.FromRgb(0x7F, 0x7F, 0x7F);
        t[9] = Color.FromRgb(0xFF, 0x00, 0x00);
        t[10] = Color.FromRgb(0x00, 0xFF, 0x00);
        t[11] = Color.FromRgb(0xFF, 0xFF, 0x00);
        t[12] = Color.FromRgb(0x5C, 0x5C, 0xFF);
        t[13] = Color.FromRgb(0xFF, 0x00, 0xFF);
        t[14] = Color.FromRgb(0x00, 0xFF, 0xFF);
        t[15] = Color.FromRgb(0xFF, 0xFF, 0xFF);

        ReadOnlySpan<byte> steps = stackalloc byte[] { 0, 0x5F, 0x87, 0xAF, 0xD7, 0xFF };
        for (var r = 0; r < 6; r++)
        {
            for (var g = 0; g < 6; g++)
            {
                for (var b = 0; b < 6; b++)
                {
                    t[16 + 36 * r + 6 * g + b] = Color.FromRgb(steps[r], steps[g], steps[b]);
                }
            }
        }

        for (var i = 0; i < 24; i++)
        {
            var v = (byte)(8 + i * 10);
            t[232 + i] = Color.FromRgb(v, v, v);
        }

        return t;
    }
}
