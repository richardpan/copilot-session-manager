using System.Collections.Generic;
using System.Text;
using CopilotSessionManager.Terminal;
using FluentAssertions;
using Xunit;

namespace CopilotSessionManager.Terminal.Tests;

public class SelectionTextExtractorTests
{
    [Fact]
    public void Empty_selection_returns_empty_string()
    {
        var buffer = NewBufferWithText(new[] { "hello world" });
        var sel = new TerminalSelection(1, 3, 1, 3);
        SelectionTextExtractor.Extract(buffer, sel).Should().Be(string.Empty);
    }

    [Fact]
    public void Single_row_selection_returns_substring_inclusive_of_both_endpoints()
    {
        var buffer = NewBufferWithText(new[] { "hello world" });
        var sel = new TerminalSelection(1, 1, 1, 5);
        SelectionTextExtractor.Extract(buffer, sel).Should().Be("hello");
    }

    [Fact]
    public void Trailing_spaces_in_row_are_trimmed()
    {
        var buffer = NewBufferWithText(new[] { "hi" }, columns: 20);
        var sel = new TerminalSelection(1, 1, 1, 20);
        SelectionTextExtractor.Extract(buffer, sel).Should().Be("hi");
    }

    [Fact]
    public void Multi_row_selection_joins_rows_with_crlf()
    {
        var buffer = NewBufferWithText(new[] { "line one", "line two", "line three" });
        var sel = new TerminalSelection(1, 1, 3, 10);
        SelectionTextExtractor.Extract(buffer, sel)
            .Should().Be("line one\r\nline two\r\nline three");
    }

    [Fact]
    public void Multi_row_selection_uses_per_row_endpoints()
    {
        // Row 1 from column 6, full row 2, row 3 up to column 4.
        var buffer = NewBufferWithText(new[]
        {
            "hello world",  // start at column 6 -> " world"
            "second row",
            "third row",    // up to column 4 -> "thir"
        });
        var sel = new TerminalSelection(1, 6, 3, 4);
        SelectionTextExtractor.Extract(buffer, sel)
            .Should().Be(" world\r\nsecond row\r\nthir");
    }

    [Fact]
    public void Reverse_drag_is_normalized_to_reading_order()
    {
        var buffer = NewBufferWithText(new[] { "abcdef", "ghijkl" });
        var sel = new TerminalSelection(2, 4, 1, 2);
        SelectionTextExtractor.Extract(buffer, sel)
            .Should().Be("bcdef\r\nghij");
    }

    [Fact]
    public void Selection_clamped_to_buffer_bounds()
    {
        var buffer = NewBufferWithText(new[] { "ab" });
        var sel = new TerminalSelection(1, 1, 999, 999);
        SelectionTextExtractor.Extract(buffer, sel).Should().Be("ab");
    }

    // -- #179 rectangular (block) selection -----------------------------------

    [Fact]
    public void Rectangle_selection_extracts_same_column_slice_per_row()
    {
        var buffer = NewBufferWithText(new[]
        {
            "alpha beta",
            "gamma delta",
            "epsilon zeta",
        });
        // Columns 3..6 from each row: "pha ", "mma ", "silo" — trailing
        // spaces trimmed per row to match stream-mode behaviour.
        var sel = new TerminalSelection(1, 3, 3, 6) { Mode = SelectionMode.Rectangle };
        SelectionTextExtractor.Extract(buffer, sel)
            .Should().Be("pha\r\nmma\r\nsilo");
    }

    [Fact]
    public void Rectangle_selection_normalizes_reversed_corners()
    {
        var buffer = NewBufferWithText(new[]
        {
            "alpha beta",
            "gamma delta",
            "epsilon zeta",
        });
        // Anchor at bottom-right, focus at top-left -> same rectangle.
        var sel = new TerminalSelection(3, 6, 1, 3) { Mode = SelectionMode.Rectangle };
        SelectionTextExtractor.Extract(buffer, sel)
            .Should().Be("pha\r\nmma\r\nsilo");
    }

    [Fact]
    public void Rectangle_selection_trims_trailing_spaces_per_row()
    {
        var buffer = NewBufferWithText(new[]
        {
            "ab",
            "cd",
        }, columns: 20);
        // Slice columns 1..10: each row has two letters followed by 8 spaces; trimmed.
        var sel = new TerminalSelection(1, 1, 2, 10) { Mode = SelectionMode.Rectangle };
        SelectionTextExtractor.Extract(buffer, sel)
            .Should().Be("ab\r\ncd");
    }

    [Fact]
    public void Rectangle_selection_single_column_yields_one_cell_per_row()
    {
        var buffer = NewBufferWithText(new[]
        {
            "abc",
            "def",
            "ghi",
        });
        var sel = new TerminalSelection(1, 2, 3, 2) { Mode = SelectionMode.Rectangle };
        SelectionTextExtractor.Extract(buffer, sel)
            .Should().Be("b\r\ne\r\nh");
    }

    [Fact]
    public void Rectangle_selection_clamped_to_buffer_bounds()
    {
        var buffer = NewBufferWithText(new[] { "abc", "def" });
        var sel = new TerminalSelection(-3, -3, 999, 999) { Mode = SelectionMode.Rectangle };
        SelectionTextExtractor.Extract(buffer, sel)
            .Should().Be("abc\r\ndef");
    }

    private static ScreenBuffer NewBufferWithText(string[] rows, int? columns = null)
    {
        var cols = columns ?? GetMaxLength(rows);
        var buffer = new ScreenBuffer(rows: rows.Length, columns: cols);
        var events = new List<VtEvent>();
        var parser = new VtParser(events.Add);
        for (var i = 0; i < rows.Length; i++)
        {
            // Move to (row, 1) and write the row text.
            var line = rows[i];
            var seq = $"\u001B[{i + 1};1H{line}";
            parser.Feed(Encoding.UTF8.GetBytes(seq));
        }
        buffer.ApplyAll(events);
        return buffer;
    }

    private static int GetMaxLength(string[] rows)
    {
        var max = 0;
        foreach (var r in rows)
        {
            if (r.Length > max)
            {
                max = r.Length;
            }
        }
        return max;
    }
}
