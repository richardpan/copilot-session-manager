using System;
using System.Collections.Generic;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CopilotSessionManager.Terminal;
using FluentAssertions;

namespace CopilotSessionManager.Terminal.Wpf.Tests;

/// <summary>
/// Phase 3B regressions: dirty-row incremental dispatch + cursor visual
/// + blink. The whole-buffer rendering path is covered by
/// <see cref="TerminalControlRenderTests"/>.
/// </summary>
public class DirtyRowAndCursorTests
{
    [Fact]
    public void Dispatched_incremental_repaint_only_touches_dirty_rows() => StaRunner.Run(() =>
    {
        var control = NewControl();
        var buffer = new ScreenBuffer(rows: 6, columns: 12);
        control.Buffer = buffer;
        Layout(control);
        ForceRender(control);

        // Hide the cursor so it doesn't smudge the row it's parked on
        // when content arrives elsewhere — this test is about cell-content
        // dirty-row isolation, not the cursor visual.
        FeedRaw(buffer, "\x1b[?25l");
        control.FlushPendingRender();

        var first = SnapshotToBitmap(control);
        var firstRowPixels = CapturePerRowPixelHashes(first, control.Metrics!, buffer.Rows);

        FeedAt(buffer, row: 3, column: 1, text: "hello");
        control.FlushPendingRender();

        var second = SnapshotToBitmap(control);
        var secondRowPixels = CapturePerRowPixelHashes(second, control.Metrics!, buffer.Rows);

        for (var row = 0; row < buffer.Rows; row++)
        {
            if (row == 2) // 0-based row 2 == 1-based row 3
            {
                secondRowPixels[row].Should().NotEqual(
                    firstRowPixels[row],
                    "row 3 was the only row mutated and must visibly differ");
            }
            else
            {
                secondRowPixels[row].Should().Equal(
                    firstRowPixels[row],
                    $"row {row + 1} was not mutated and must remain pixel-identical");
            }
        }
    });

    [Fact]
    public void FlushPendingRender_clears_buffer_dirty_flags() => StaRunner.Run(() =>
    {
        var control = NewControl();
        var buffer = new ScreenBuffer(rows: 4, columns: 8);
        control.Buffer = buffer;
        Layout(control);
        ForceRender(control);
        buffer.HasDirtyRows.Should().BeFalse("initial FullRepaint clears dirty");

        FeedAt(buffer, row: 2, column: 1, text: "x");
        buffer.HasDirtyRows.Should().BeTrue();

        control.FlushPendingRender();

        buffer.HasDirtyRows.Should().BeFalse();
    });

    [Fact]
    public void Cursor_visual_renders_a_block_when_cursor_is_visible() => StaRunner.Run(() =>
    {
        var control = NewControl();
        var buffer = new ScreenBuffer(rows: 4, columns: 8);
        control.Buffer = buffer;
        Layout(control);
        ForceRender(control);

        buffer.CursorVisible.Should().BeTrue();
        control.CursorBlinkOn.Should().BeTrue();
        control.CursorVisual.ContentBounds.IsEmpty.Should().BeFalse(
            "the cursor block must be drawn while visible and in its on-phase");
    });

    [Fact]
    public void Cursor_visual_renders_at_the_buffer_cursor_position() => StaRunner.Run(() =>
    {
        var control = NewControl();
        var buffer = new ScreenBuffer(rows: 4, columns: 8);
        control.Buffer = buffer;
        Layout(control);
        ForceRender(control);

        FeedRaw(buffer, "\x1b[3;5H");
        control.FlushPendingRender();

        var bounds = control.CursorVisual.ContentBounds;
        var m = control.Metrics!;
        bounds.X.Should().BeApproximately(4 * m.CellWidth, 0.5); // column 5 → offset 4
        bounds.Y.Should().BeApproximately(2 * m.CellHeight, 0.5); // row 3 → offset 2
        bounds.Width.Should().BeApproximately(m.CellWidth, 0.5);
        bounds.Height.Should().BeApproximately(m.CellHeight, 0.5);
    });

    [Fact]
    public void Cursor_visual_disappears_on_off_blink_phase() => StaRunner.Run(() =>
    {
        var control = NewControl();
        var buffer = new ScreenBuffer(rows: 4, columns: 8);
        control.Buffer = buffer;
        Layout(control);
        ForceRender(control);

        control.CursorVisual.ContentBounds.IsEmpty.Should().BeFalse();

        control.AdvanceCursorBlinkForTest();

        control.CursorBlinkOn.Should().BeFalse();
        control.CursorVisual.ContentBounds.IsEmpty.Should().BeTrue();

        control.AdvanceCursorBlinkForTest();

        control.CursorBlinkOn.Should().BeTrue();
        control.CursorVisual.ContentBounds.IsEmpty.Should().BeFalse();
    });

    [Fact]
    public void Cursor_visual_is_empty_when_cursor_is_hidden_via_DECTCEM() => StaRunner.Run(() =>
    {
        var control = NewControl();
        var buffer = new ScreenBuffer(rows: 4, columns: 8);
        control.Buffer = buffer;
        Layout(control);
        ForceRender(control);
        control.CursorVisual.ContentBounds.IsEmpty.Should().BeFalse();

        FeedRaw(buffer, "\x1b[?25l");
        control.FlushPendingRender();

        buffer.CursorVisible.Should().BeFalse();
        control.CursorVisual.ContentBounds.IsEmpty.Should().BeTrue();
    });

    [Fact]
    public void Resize_triggers_full_repaint_through_invalidation_event() => StaRunner.Run(() =>
    {
        var control = NewControl();
        var buffer = new ScreenBuffer(rows: 4, columns: 8);
        control.Buffer = buffer;
        Layout(control);
        ForceRender(control);
        VisualTreeHelper.GetChildrenCount(control).Should().Be(5);

        buffer.Resize(7, 12);
        control.FlushPendingRender();
        control.InvalidateMeasure();
        Layout(control);
        ForceRender(control);

        VisualTreeHelper.GetChildrenCount(control).Should().Be(8); // 7 rows + cursor
    });

    private static TerminalControl NewControl() => new()
    {
        FontFamily = new FontFamily("Cascadia Mono, Consolas, Courier New"),
        FontSize = 14.0,
    };

    private static void Layout(TerminalControl control)
    {
        control.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        control.Arrange(new Rect(control.DesiredSize));
    }

    private static void ForceRender(TerminalControl control)
    {
        var width = Math.Max((int)Math.Ceiling(control.DesiredSize.Width), 1);
        var height = Math.Max((int)Math.Ceiling(control.DesiredSize.Height), 1);
        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(control);
    }

    private static BitmapSource SnapshotToBitmap(TerminalControl control)
    {
        var width = Math.Max((int)Math.Ceiling(control.DesiredSize.Width), 1);
        var height = Math.Max((int)Math.Ceiling(control.DesiredSize.Height), 1);
        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(control);
        return rtb;
    }

    private static List<byte[]> CapturePerRowPixelHashes(BitmapSource bitmap, CellMetrics metrics, int rows)
    {
        var hashes = new List<byte[]>(rows);
        var rowHeightPx = (int)Math.Max(1, Math.Floor(metrics.CellHeight));
        var width = bitmap.PixelWidth;
        var stride = width * 4;
        for (var r = 0; r < rows; r++)
        {
            var top = (int)Math.Round(r * metrics.CellHeight);
            var available = bitmap.PixelHeight - top;
            var height = Math.Min(rowHeightPx, Math.Max(0, available));
            if (height <= 0)
            {
                hashes.Add(Array.Empty<byte>());
                continue;
            }
            var buffer = new byte[stride * height];
            bitmap.CopyPixels(new Int32Rect(0, top, width, height), buffer, stride, 0);
            hashes.Add(buffer);
        }
        return hashes;
    }

    private static void FeedAt(ScreenBuffer buffer, int row, int column, string text)
    {
        FeedRaw(buffer, $"\x1b[{row};{column}H{text}");
    }

    private static void FeedRaw(ScreenBuffer buffer, string raw)
    {
        var events = new List<VtEvent>();
        var parser = new VtParser(events.Add);
        parser.Feed(Encoding.ASCII.GetBytes(raw));
        buffer.ApplyAll(events);
    }
}
