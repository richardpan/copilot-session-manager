using System.Collections.Generic;
using System.Text;
using System.Windows.Input;
using System.Windows.Media;
using CopilotSessionManager.Terminal;
using FluentAssertions;
using Xunit;

namespace CopilotSessionManager.Terminal.Wpf.Tests;

public class TerminalControlSelectionTests
{
    [Fact]
    public void Mouse_drag_updates_Selection_property_and_raises_event() => StaRunner.Run(() =>
    {
        var control = NewControlWithBuffer(rows: 6, columns: 12);
        var changeCount = 0;
        control.SelectionChanged += (_, _) => changeCount++;

        control.DispatchMouseDownForTest(2, 3);
        control.DispatchMouseDragForTest(4, 7);
        control.DispatchMouseUpForTest();

        control.Selection.Should().NotBeNull();
        control.Selection!.AnchorRow.Should().Be(2);
        control.Selection.AnchorColumn.Should().Be(3);
        control.Selection.FocusRow.Should().Be(4);
        control.Selection.FocusColumn.Should().Be(7);
        // At least two events: begin + update (end does not change selection).
        changeCount.Should().BeGreaterThanOrEqualTo(2);
    });

    [Fact]
    public void GetSelectedText_returns_extracted_string() => StaRunner.Run(() =>
    {
        var control = NewControlWithText(new[] { "hello world" });

        control.DispatchMouseDownForTest(1, 1);
        control.DispatchMouseDragForTest(1, 5);
        control.DispatchMouseUpForTest();

        control.GetSelectedText().Should().Be("hello");
    });

    [Fact]
    public void GetSelectedText_returns_empty_when_no_selection() => StaRunner.Run(() =>
    {
        var control = NewControlWithBuffer(rows: 4, columns: 12);
        control.GetSelectedText().Should().Be(string.Empty);
    });

    [Fact]
    public void CopyToClipboard_writes_selected_text_to_clipboard_abstraction() => StaRunner.Run(() =>
    {
        var control = NewControlWithText(new[] { "abcdef" });
        var fake = new FakeClipboard();
        control.Clipboard = fake;

        control.DispatchMouseDownForTest(1, 2);
        control.DispatchMouseDragForTest(1, 5);
        control.DispatchMouseUpForTest();
        control.CopyToClipboard();

        fake.Text.Should().Be("bcde");
    });

    [Fact]
    public void CopyToClipboard_is_no_op_when_selection_is_empty() => StaRunner.Run(() =>
    {
        var control = NewControlWithText(new[] { "abcdef" });
        var fake = new FakeClipboard { Text = "untouched" };
        control.Clipboard = fake;

        control.CopyToClipboard();

        fake.Text.Should().Be("untouched");
    });

    [Fact]
    public void PasteFromClipboard_routes_through_Paste() => StaRunner.Run(() =>
    {
        var control = NewControlWithBuffer(rows: 4, columns: 12);
        control.Clipboard = new FakeClipboard { Text = "hello" };
        var captured = HookInputBytes(control);

        control.PasteFromClipboard();

        captured.Single().Should().Equal(Encoding.UTF8.GetBytes("hello"));
    });

    [Fact]
    public void PasteFromClipboard_honours_bracketed_paste() => StaRunner.Run(() =>
    {
        var buffer = new ScreenBuffer(rows: 4, columns: 12);
        var events = new List<VtEvent>();
        var parser = new VtParser(events.Add);
        parser.Feed(Encoding.ASCII.GetBytes("\u001B[?2004h"));
        buffer.ApplyAll(events);
        buffer.BracketedPasteEnabled.Should().BeTrue();

        var control = NewControl();
        control.Buffer = buffer;
        control.Clipboard = new FakeClipboard { Text = "x" };
        var captured = new List<string>();
        control.InputProduced += (_, e) =>
            captured.Add(Encoding.UTF8.GetString(e.Bytes.Span));

        control.PasteFromClipboard();

        captured.Single().Should().Be("\u001B[200~x\u001B[201~");
    });

    [Fact]
    public void Ctrl_C_with_selection_copies_and_does_not_emit_SIGINT() => StaRunner.Run(() =>
    {
        var control = NewControlWithText(new[] { "abcdef" });
        var fake = new FakeClipboard();
        control.Clipboard = fake;
        var captured = HookInputBytes(control);

        control.DispatchMouseDownForTest(1, 1);
        control.DispatchMouseDragForTest(1, 3);
        control.DispatchMouseUpForTest();

        control.DispatchKeyForTest(Key.C, ModifierKeys.Control).Should().BeTrue();

        fake.Text.Should().Be("abc");
        captured.Should().BeEmpty("Ctrl+C with a selection must not send the SIGINT byte");
        control.Selection.Should().BeNull("the selection is cleared once it has been copied");
    });

    [Fact]
    public void Ctrl_C_without_selection_emits_SIGINT_byte() => StaRunner.Run(() =>
    {
        var control = NewControlWithBuffer(rows: 4, columns: 12);
        var captured = HookInputBytes(control);

        control.DispatchKeyForTest(Key.C, ModifierKeys.Control).Should().BeTrue();

        captured.Single().Should().Equal(new byte[] { 0x03 });
    });

    [Fact]
    public void Ctrl_V_pastes_from_clipboard_instead_of_emitting_C0() => StaRunner.Run(() =>
    {
        var control = NewControlWithBuffer(rows: 4, columns: 12);
        control.Clipboard = new FakeClipboard { Text = "pasted" };
        var captured = HookInputBytes(control);

        control.DispatchKeyForTest(Key.V, ModifierKeys.Control).Should().BeTrue();

        captured.Single().Should().Equal(Encoding.UTF8.GetBytes("pasted"));
    });

    [Fact]
    public void Typing_clears_an_existing_selection() => StaRunner.Run(() =>
    {
        var control = NewControlWithText(new[] { "abcdef" });

        control.DispatchMouseDownForTest(1, 1);
        control.DispatchMouseDragForTest(1, 4);
        control.DispatchMouseUpForTest();
        control.Selection.Should().NotBeNull();

        control.DispatchTextInputForTest("x", ModifierKeys.None).Should().BeTrue();

        control.Selection.Should().BeNull();
    });

    [Fact]
    public void ClearSelection_removes_visual_overlay() => StaRunner.Run(() =>
    {
        var control = NewControlWithText(new[] { "abcdef" });
        Layout(control);
        ForceRender(control);

        control.DispatchMouseDownForTest(1, 1);
        control.DispatchMouseDragForTest(1, 3);
        control.DispatchMouseUpForTest();

        control.SelectionVisual.ContentBounds.IsEmpty
            .Should().BeFalse("highlight rect should have been painted");

        control.ClearSelection();

        control.SelectionVisual.ContentBounds.IsEmpty
            .Should().BeTrue("selection visual is empty once selection is cleared");
        control.Selection.Should().BeNull();
    });

    // -- #178: shift-extend, double-click word, triple-click row --------

    [Fact]
    public void ExtendSelectionTo_keeps_anchor_and_moves_focus_on_existing_selection() => StaRunner.Run(() =>
    {
        var control = NewControlWithText(new[] { "hello world here" });
        control.DispatchMouseDownForTest(1, 1);
        control.DispatchMouseDragForTest(1, 5);
        control.DispatchMouseUpForTest();

        control.GetSelectedText().Should().Be("hello");

        control.ExtendSelectionTo(1, 11);

        control.Selection.Should().NotBeNull();
        control.Selection!.AnchorRow.Should().Be(1);
        control.Selection.AnchorColumn.Should().Be(1, "anchor must not move on extend");
        control.Selection.FocusRow.Should().Be(1);
        control.Selection.FocusColumn.Should().Be(11);
        control.GetSelectedText().Should().Be("hello world");
    });

    [Fact]
    public void ExtendSelectionTo_starts_a_new_selection_when_none_exists() => StaRunner.Run(() =>
    {
        var control = NewControlWithText(new[] { "abcdef" });
        control.Selection.Should().BeNull();

        control.ExtendSelectionTo(1, 3);

        control.Selection.Should().NotBeNull();
        control.Selection!.AnchorColumn.Should().Be(3);
        control.Selection.FocusColumn.Should().Be(3);
    });

    [Fact]
    public void ExtendSelectionTo_continues_to_track_subsequent_drag_updates() => StaRunner.Run(() =>
    {
        var control = NewControlWithText(new[] { "hello world here" });
        control.DispatchMouseDownForTest(1, 1);
        control.DispatchMouseDragForTest(1, 5);
        control.DispatchMouseUpForTest();

        control.ExtendSelectionTo(1, 11);
        // The shift-click leaves the control in drag-extend mode, so a
        // subsequent mouse-move continues to move the focus end.
        control.DispatchMouseDragForTest(1, 13);

        control.Selection!.AnchorColumn.Should().Be(1);
        control.Selection.FocusColumn.Should().Be(13);
    });

    [Fact]
    public void SelectWord_selects_the_word_under_the_cursor() => StaRunner.Run(() =>
    {
        var control = NewControlWithText(new[] { "the quick brown fox" });
        control.SelectWord(1, 6);

        control.Selection.Should().NotBeNull();
        control.Selection!.AnchorRow.Should().Be(1);
        control.Selection.FocusRow.Should().Be(1);
        control.GetSelectedText().Should().Be("quick");
    });

    [Fact]
    public void SelectWord_on_whitespace_yields_single_cell_selection() => StaRunner.Run(() =>
    {
        var control = NewControlWithText(new[] { "a b c" });
        control.SelectWord(1, 2);

        control.Selection.Should().NotBeNull();
        control.Selection!.AnchorColumn.Should().Be(2);
        control.Selection.FocusColumn.Should().Be(2);
    });

    [Fact]
    public void SelectRow_spans_the_whole_row_width() => StaRunner.Run(() =>
    {
        var control = NewControlWithBuffer(rows: 4, columns: 12);
        control.SelectRow(2);

        control.Selection.Should().NotBeNull();
        control.Selection!.AnchorRow.Should().Be(2);
        control.Selection.AnchorColumn.Should().Be(1);
        control.Selection.FocusRow.Should().Be(2);
        control.Selection.FocusColumn.Should().Be(12);
    });

    [Fact]
    public void SelectRow_followed_by_drag_extends_to_other_rows() => StaRunner.Run(() =>
    {
        var control = NewControlWithBuffer(rows: 6, columns: 10);
        control.SelectRow(2);
        control.DispatchMouseDragForTest(4, 3);

        // SelectRow leaves the control in selecting mode so dragging
        // continues the selection per cell (the row anchor stays).
        control.Selection!.AnchorRow.Should().Be(2);
        control.Selection.AnchorColumn.Should().Be(1);
        control.Selection.FocusRow.Should().Be(4);
        control.Selection.FocusColumn.Should().Be(3);
    });

    [Fact]
    public void Existing_selection_control_tests_still_pass_after_178_changes() => StaRunner.Run(() =>
    {
        // Sanity guard: the shift / double / triple paths must not have
        // broken the bare-click happy path.
        var control = NewControlWithText(new[] { "hello world" });
        control.DispatchMouseDownForTest(1, 1);
        control.DispatchMouseDragForTest(1, 5);
        control.DispatchMouseUpForTest();
        control.GetSelectedText().Should().Be("hello");
    });

    // -- #179 Alt+drag block (rectangular) selection --------------------------

    [Fact]
    public void BeginSelection_with_Rectangle_mode_sets_Mode() => StaRunner.Run(() =>
    {
        var control = NewControlWithBuffer(rows: 6, columns: 12);
        control.BeginSelection(2, 3, SelectionMode.Rectangle);

        control.Selection.Should().NotBeNull();
        control.Selection!.Mode.Should().Be(SelectionMode.Rectangle);
    });

    [Fact]
    public void BeginSelection_default_overload_defaults_to_Stream_mode() => StaRunner.Run(() =>
    {
        var control = NewControlWithBuffer(rows: 6, columns: 12);
        control.BeginSelection(2, 3);

        control.Selection!.Mode.Should().Be(SelectionMode.Stream);
    });

    [Fact]
    public void Rectangle_selection_preserves_mode_through_drag() => StaRunner.Run(() =>
    {
        var control = NewControlWithBuffer(rows: 6, columns: 12);
        control.BeginSelection(2, 3, SelectionMode.Rectangle);
        control.DispatchMouseDragForTest(5, 9);

        control.Selection.Should().NotBeNull();
        control.Selection!.Mode.Should().Be(SelectionMode.Rectangle);
        control.Selection.AnchorRow.Should().Be(2);
        control.Selection.AnchorColumn.Should().Be(3);
        control.Selection.FocusRow.Should().Be(5);
        control.Selection.FocusColumn.Should().Be(9);
    });

    [Fact]
    public void Rectangle_selection_GetSelectedText_yields_column_slices_per_row() => StaRunner.Run(() =>
    {
        var control = NewControlWithText(new[]
        {
            "alpha beta",
            "gamma delta",
            "epsilon zeta",
        });
        control.BeginSelection(1, 3, SelectionMode.Rectangle);
        control.DispatchMouseDragForTest(3, 6);
        control.DispatchMouseUpForTest();

        control.GetSelectedText().Should().Be("pha\r\nmma\r\nsilo");
    });

    [Fact]
    public void Rectangle_selection_reversed_drag_yields_normalized_text() => StaRunner.Run(() =>
    {
        var control = NewControlWithText(new[]
        {
            "alpha beta",
            "gamma delta",
            "epsilon zeta",
        });
        // Drag from bottom-right corner up to top-left of the same block.
        control.BeginSelection(3, 6, SelectionMode.Rectangle);
        control.DispatchMouseDragForTest(1, 3);
        control.DispatchMouseUpForTest();

        control.GetSelectedText().Should().Be("pha\r\nmma\r\nsilo");
    });

    [Fact]
    public void Rectangle_selection_single_column_yields_one_glyph_per_row() => StaRunner.Run(() =>
    {
        var control = NewControlWithText(new[] { "abc", "def", "ghi" });
        control.BeginSelection(1, 2, SelectionMode.Rectangle);
        control.DispatchMouseDragForTest(3, 2);

        control.GetSelectedText().Should().Be("b\r\ne\r\nh");
    });

    [Fact]
    public void Stream_selection_unaffected_by_block_changes() => StaRunner.Run(() =>
    {
        // Regression guard: a plain BeginSelection -> drag should still
        // produce the legacy reading-order text (full middle rows, etc).
        var control = NewControlWithText(new[]
        {
            "alpha beta",
            "gamma delta",
            "epsilon zeta",
        });
        control.BeginSelection(1, 3);
        control.DispatchMouseDragForTest(3, 6);
        control.DispatchMouseUpForTest();

        control.Selection!.Mode.Should().Be(SelectionMode.Stream);
        control.GetSelectedText().Should().Be("pha beta\r\ngamma delta\r\nepsilo");
    });

    private static TerminalControl NewControl() => new()
    {
        FontFamily = new FontFamily("Cascadia Mono, Consolas, Courier New"),
        FontSize = 14.0,
    };

    private static TerminalControl NewControlWithBuffer(int rows, int columns)
    {
        var control = NewControl();
        control.Buffer = new ScreenBuffer(rows, columns);
        return control;
    }

    private static TerminalControl NewControlWithText(string[] rows)
    {
        var maxLen = 0;
        foreach (var r in rows)
        {
            if (r.Length > maxLen)
            {
                maxLen = r.Length;
            }
        }
        var buffer = new ScreenBuffer(rows: rows.Length, columns: maxLen);
        var events = new List<VtEvent>();
        var parser = new VtParser(events.Add);
        for (var i = 0; i < rows.Length; i++)
        {
            parser.Feed(Encoding.UTF8.GetBytes($"\u001B[{i + 1};1H{rows[i]}"));
        }
        buffer.ApplyAll(events);
        var control = NewControl();
        control.Buffer = buffer;
        return control;
    }

    private static void Layout(TerminalControl control)
    {
        control.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        control.Arrange(new System.Windows.Rect(control.DesiredSize));
    }

    private static void ForceRender(TerminalControl control)
    {
        var w = (int)System.Math.Ceiling(control.RenderSize.Width);
        var h = (int)System.Math.Ceiling(control.RenderSize.Height);
        if (w <= 0)
        {
            w = 1;
        }
        if (h <= 0)
        {
            h = 1;
        }
        var rt = new System.Windows.Media.Imaging.RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rt.Render(control);
    }

    private static List<byte[]> HookInputBytes(TerminalControl control)
    {
        var captured = new List<byte[]>();
        control.InputProduced += (_, e) => captured.Add(e.Bytes.ToArray());
        return captured;
    }

    private sealed class FakeClipboard : ITerminalClipboard
    {
        public string? Text { get; set; }

        public string? GetText() => Text;
        public void SetText(string text) => Text = text;
    }
}
