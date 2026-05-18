using System;
using System.Text;

namespace CopilotSessionManager.Terminal;

/// <summary>
/// Resolves word boundaries inside a <see cref="ScreenBuffer"/> row for
/// double-click word-selection (issue #178). "Word" is a maximal run of
/// non-whitespace cells; whitespace under the cursor returns a single
/// degenerate range so the caller can still produce a selection of one
/// cell if they wish.
/// </summary>
/// <remarks>
/// <para>
/// This is a deliberately simple whitespace separator. Grapheme cluster
/// awareness, language-specific word breaks, and URL detection are out
/// of scope here - revisit when a use case shows up.
/// </para>
/// <para>
/// The finder is rune-aware (cells store a <see cref="Rune"/>); CJK and
/// emoji cells are treated as non-whitespace and therefore included in
/// the word.
/// </para>
/// </remarks>
public static class WordBoundaryFinder
{
    /// <summary>
    /// Find the inclusive column range of the word covering
    /// <paramref name="column"/> on <paramref name="row"/>. Coordinates
    /// are 1-based to match the rest of the <see cref="ScreenBuffer"/>
    /// API. If the cell at the cursor is whitespace, returns
    /// <c>(column, column)</c>. Out-of-range inputs are clamped.
    /// </summary>
    public static (int StartColumn, int EndColumn) FindWord(ScreenBuffer buffer, int row, int column)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        row = Math.Clamp(row, 1, buffer.Rows);
        column = Math.Clamp(column, 1, buffer.Columns);

        if (IsWhitespace(buffer.GetCell(row, column).Glyph))
        {
            return (column, column);
        }

        var start = column;
        while (start > 1 && !IsWhitespace(buffer.GetCell(row, start - 1).Glyph))
        {
            start--;
        }

        var end = column;
        while (end < buffer.Columns && !IsWhitespace(buffer.GetCell(row, end + 1).Glyph))
        {
            end++;
        }

        return (start, end);
    }

    private static bool IsWhitespace(Rune glyph)
    {
        // Treat the cell-empty rune (NUL) and ASCII space as separators;
        // delegate everything else to Rune's Unicode whitespace check so
        // tabs, no-break spaces, etc. count too.
        if (glyph.Value == 0)
        {
            return true;
        }
        return Rune.IsWhiteSpace(glyph);
    }
}
