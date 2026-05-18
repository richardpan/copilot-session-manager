using CopilotSessionManager.Terminal;
using FluentAssertions;
using Xunit;

namespace CopilotSessionManager.Terminal.Tests;

public class WordBoundaryFinderTests
{
    [Fact]
    public void Finds_word_in_the_middle_of_a_row()
    {
        var buf = NewBufferWithRow("the quick brown fox");

        WordBoundaryFinder.FindWord(buf, 1, 6).Should().Be((5, 9), "'quick' spans cols 5-9");
        WordBoundaryFinder.FindWord(buf, 1, 5).Should().Be((5, 9), "start of word");
        WordBoundaryFinder.FindWord(buf, 1, 9).Should().Be((5, 9), "end of word");
    }

    [Fact]
    public void Finds_word_at_left_edge()
    {
        var buf = NewBufferWithRow("hello world");

        WordBoundaryFinder.FindWord(buf, 1, 1).Should().Be((1, 5));
        WordBoundaryFinder.FindWord(buf, 1, 3).Should().Be((1, 5));
    }

    [Fact]
    public void Finds_word_at_right_edge()
    {
        var buf = NewBufferWithRow("hello world");

        WordBoundaryFinder.FindWord(buf, 1, 11).Should().Be((7, 11));
    }

    [Fact]
    public void Whitespace_under_cursor_returns_degenerate_single_cell_range()
    {
        var buf = NewBufferWithRow("a b c");
        WordBoundaryFinder.FindWord(buf, 1, 2).Should().Be((2, 2), "space at col 2");
    }

    [Fact]
    public void Empty_cell_under_cursor_is_treated_as_whitespace()
    {
        var buf = new ScreenBuffer(rows: 1, columns: 10);
        WordBoundaryFinder.FindWord(buf, 1, 5).Should().Be((5, 5));
    }

    [Fact]
    public void Out_of_range_coordinates_are_clamped()
    {
        var buf = NewBufferWithRow("hello");
        WordBoundaryFinder.FindWord(buf, 1, 0).Should().Be((1, 5), "negative column clamps to 1");
        WordBoundaryFinder.FindWord(buf, 1, 999).Should().Be((1, 5), "out-of-range column clamps to end");
        WordBoundaryFinder.FindWord(buf, 999, 1).Should().Be((1, 5), "out-of-range row clamps");
    }

    [Fact]
    public void Punctuation_is_part_of_the_word_because_whitespace_is_the_only_separator()
    {
        var buf = NewBufferWithRow("foo.bar/baz");
        WordBoundaryFinder.FindWord(buf, 1, 5).Should().Be((1, 11), "path-like token has no internal whitespace");
    }

    [Fact]
    public void Tab_is_treated_as_whitespace()
    {
        // HT advances the cursor to the next tab stop (col 9 by default),
        // so "bar" lands at col 9 with empty cells in 4-8. The word
        // covering col 1 is "foo" cols 1-3.
        var buf = NewBufferWithRow("foo\tbar", columns: 20);
        WordBoundaryFinder.FindWord(buf, 1, 1).Should().Be((1, 3), "empty cells between foo and bar terminate the word");
        WordBoundaryFinder.FindWord(buf, 1, 9).Should().Be((9, 11), "bar at the next tab stop");
    }

    private static ScreenBuffer NewBufferWithRow(string text, int? columns = null)
    {
        var buffer = new ScreenBuffer(rows: 1, columns: columns ?? text.Length);
        var parser = new VtParser(e => buffer.Apply(e));
        parser.Feed(System.Text.Encoding.UTF8.GetBytes(text));
        return buffer;
    }
}
