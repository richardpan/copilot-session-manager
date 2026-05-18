using System;
using System.Text;

namespace CopilotSessionManager.Terminal;

/// <summary>
/// Extracts plain text from a region of a <see cref="ScreenBuffer"/>
/// described by a <see cref="TerminalSelection"/>. Trailing spaces are
/// trimmed per row so that copying a sparsely-populated screen does not
/// emit megabyte-long runs of padding.
/// </summary>
public static class SelectionTextExtractor
{
    /// <summary>Line separator inserted between rows of multi-row selections.</summary>
    public const string LineSeparator = "\r\n";

    /// <summary>
    /// Walk the cells covered by <paramref name="selection"/> and return
    /// the user-visible text. Both endpoints are inclusive. Selection
    /// coordinates are clamped to the buffer's row and column ranges.
    /// </summary>
    public static string Extract(ScreenBuffer buffer, TerminalSelection selection)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentNullException.ThrowIfNull(selection);

        if (selection.IsEmpty)
        {
            return string.Empty;
        }

        if (selection.Mode == SelectionMode.Rectangle)
        {
            return ExtractRectangle(buffer, selection);
        }

        var norm = selection.Normalize();
        var startRow = Math.Clamp(norm.AnchorRow, 1, buffer.Rows);
        var endRow = Math.Clamp(norm.FocusRow, 1, buffer.Rows);
        var startCol = Math.Clamp(norm.AnchorColumn, 1, buffer.Columns);
        var endCol = Math.Clamp(norm.FocusColumn, 1, buffer.Columns);

        var sb = new StringBuilder();
        for (var row = startRow; row <= endRow; row++)
        {
            var first = row == startRow ? startCol : 1;
            var last = row == endRow ? endCol : buffer.Columns;
            var lineStart = sb.Length;

            for (var col = first; col <= last; col++)
            {
                var glyph = buffer.GetCell(row, col).Glyph;
                sb.Append(glyph.ToString());
            }

            while (sb.Length > lineStart && sb[^1] == ' ')
            {
                sb.Length--;
            }

            if (row < endRow)
            {
                sb.Append(LineSeparator);
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Issue #179: extract a rectangular (block) selection. Each row in the
    /// span is sliced to the same column range, independent of where the
    /// drag started or ended on that row. Trailing spaces are trimmed per
    /// row to match the stream-mode behaviour.
    /// </summary>
    private static string ExtractRectangle(ScreenBuffer buffer, TerminalSelection selection)
    {
        var startRow = Math.Clamp(Math.Min(selection.AnchorRow, selection.FocusRow), 1, buffer.Rows);
        var endRow = Math.Clamp(Math.Max(selection.AnchorRow, selection.FocusRow), 1, buffer.Rows);
        var startCol = Math.Clamp(Math.Min(selection.AnchorColumn, selection.FocusColumn), 1, buffer.Columns);
        var endCol = Math.Clamp(Math.Max(selection.AnchorColumn, selection.FocusColumn), 1, buffer.Columns);

        var sb = new StringBuilder();
        for (var row = startRow; row <= endRow; row++)
        {
            var lineStart = sb.Length;
            for (var col = startCol; col <= endCol; col++)
            {
                var glyph = buffer.GetCell(row, col).Glyph;
                sb.Append(glyph.ToString());
            }

            while (sb.Length > lineStart && sb[^1] == ' ')
            {
                sb.Length--;
            }

            if (row < endRow)
            {
                sb.Append(LineSeparator);
            }
        }

        return sb.ToString();
    }
}
