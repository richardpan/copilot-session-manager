using System;
using System.Collections.Generic;
using System.Windows.Media;

namespace CopilotSessionManager.Terminal.Wpf;

/// <summary>
/// Pre-computed metrics for one monospace cell: advance width, line
/// height, baseline offset, plus a glyph-index cache backing the
/// renderer's <see cref="GlyphRun"/> construction.
/// </summary>
/// <remarks>
/// Cell metrics are derived from a <see cref="GlyphTypeface"/> at a fixed
/// em-size. The instance is immutable once built; rebuild on font change,
/// font-size change, or DPI change.
/// </remarks>
public sealed class CellMetrics
{
    private readonly Dictionary<int, ushort> _glyphIndexCache = new();
    private readonly GlyphTypeface _typeface;
    private readonly ushort _missingGlyphIndex;

    /// <summary>
    /// Build cell metrics for the supplied typeface, em-size, and DPI.
    /// </summary>
    /// <param name="typeface">Monospace glyph typeface to measure.</param>
    /// <param name="emSize">Render em-size in device-independent pixels.</param>
    /// <param name="pixelsPerDip">
    /// Pixels-per-device-independent-pixel ratio, as supplied to
    /// <see cref="GlyphRun"/>'s constructor. A nominal 96 DPI maps to 1.0.
    /// </param>
    public CellMetrics(GlyphTypeface typeface, double emSize, double pixelsPerDip)
    {
        ArgumentNullException.ThrowIfNull(typeface);
        if (emSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(emSize), "Em-size must be positive.");
        }
        if (pixelsPerDip <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pixelsPerDip), "Pixels-per-DIP must be positive.");
        }

        _typeface = typeface;
        EmSize = emSize;
        PixelsPerDip = pixelsPerDip;

        _missingGlyphIndex = typeface.CharacterToGlyphMap.TryGetValue(' ', out var spaceGlyph)
            ? spaceGlyph
            : (ushort)0;
        var sampleGlyph = typeface.CharacterToGlyphMap.TryGetValue('M', out var mGlyph)
            ? mGlyph
            : _missingGlyphIndex;
        CellWidth = typeface.AdvanceWidths[sampleGlyph] * emSize;

        CellHeight = typeface.Height * emSize;
        Baseline = typeface.Baseline * emSize;
    }

    /// <summary>Em-size the metrics were computed at, in DIPs.</summary>
    public double EmSize { get; }

    /// <summary>Pixels-per-DIP for downstream <see cref="GlyphRun"/> construction.</summary>
    public double PixelsPerDip { get; }

    /// <summary>Width of one cell in DIPs.</summary>
    public double CellWidth { get; }

    /// <summary>Height of one cell (line height) in DIPs.</summary>
    public double CellHeight { get; }

    /// <summary>Distance from the top of the cell to the glyph baseline, in DIPs.</summary>
    public double Baseline { get; }

    /// <summary>Glyph typeface used to build these metrics.</summary>
    public GlyphTypeface Typeface => _typeface;

    /// <summary>
    /// Return the glyph index for a Unicode scalar, falling back to the
    /// glyph for U+0020 when the typeface lacks coverage.
    /// </summary>
    public ushort GlyphIndexFor(int codePoint)
    {
        if (_glyphIndexCache.TryGetValue(codePoint, out var cached))
        {
            return cached;
        }
        var glyph = _typeface.CharacterToGlyphMap.TryGetValue(codePoint, out var found)
            ? found
            : _missingGlyphIndex;
        _glyphIndexCache[codePoint] = glyph;
        return glyph;
    }
}
