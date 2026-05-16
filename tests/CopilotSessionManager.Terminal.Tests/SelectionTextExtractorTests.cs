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
