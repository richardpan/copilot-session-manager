using System;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CopilotSessionManager.Terminal;
using FluentAssertions;

namespace CopilotSessionManager.Terminal.Wpf.Tests;

public class TerminalControlRenderTests
{
    [Fact]
    public void DesiredSize_matches_buffer_dimensions_times_cell_metrics() => StaRunner.Run(() =>
    {
        var control = NewControl();
        var buffer = new ScreenBuffer(rows: 24, columns: 80);
        control.Buffer = buffer;

        control.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

        control.Metrics.Should().NotBeNull();
        var m = control.Metrics!;
        control.DesiredSize.Width.Should().BeApproximately(80 * m.CellWidth, 0.5);
        control.DesiredSize.Height.Should().BeApproximately(24 * m.CellHeight, 0.5);
    });

    [Fact]
    public void Visual_children_count_equals_buffer_row_count() => StaRunner.Run(() =>
    {
        var control = NewControl();
        var buffer = new ScreenBuffer(rows: 12, columns: 40);
        control.Buffer = buffer;
        control.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

        ForceRender(control);

        VisualTreeHelper.GetChildrenCount(control).Should().Be(12);
    });

    [Fact]
    public void Rendered_glyph_at_origin_produces_non_default_pixels() => StaRunner.Run(() =>
    {
        var control = NewControl();
        var buffer = new ScreenBuffer(rows: 4, columns: 8);

        var events = new System.Collections.Generic.List<VtEvent>();
        var parser = new VtParser(events.Add);
        parser.Feed(Encoding.ASCII.GetBytes("M"));
        buffer.ApplyAll(events);

        control.Buffer = buffer;
        control.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        control.Arrange(new Rect(control.DesiredSize));
        ForceRender(control);

        var bitmap = SnapshotToBitmap(control);

        // The top-left cell should contain at least one pixel that is
        // brighter than the background — we don't lock in a hash here
        // (font rendering differs across hosts) but we do assert the
        // glyph actually painted.
        var m = control.Metrics!;
        var cellWidthPx = (int)Math.Ceiling(m.CellWidth);
        var cellHeightPx = (int)Math.Ceiling(m.CellHeight);
        cellWidthPx.Should().BeGreaterThan(0);
        cellHeightPx.Should().BeGreaterThan(0);

        var pixels = ReadPixels(bitmap, x: 0, y: 0, width: cellWidthPx, height: cellHeightPx);

        var background = control.Background;
        var foundForegroundPixel = false;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var b = pixels[i + 0];
            var g = pixels[i + 1];
            var r = pixels[i + 2];
            if (r != background.R || g != background.G || b != background.B)
            {
                foundForegroundPixel = true;
                break;
            }
        }

        foundForegroundPixel.Should().BeTrue("a rendered 'M' must produce pixels distinct from the background");
    });

    [Fact]
    public void Setting_Buffer_to_null_clears_visuals() => StaRunner.Run(() =>
    {
        var control = NewControl();
        var buffer = new ScreenBuffer(rows: 5, columns: 10);
        control.Buffer = buffer;
        control.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        ForceRender(control);
        VisualTreeHelper.GetChildrenCount(control).Should().Be(5);

        control.Buffer = null;
        ForceRender(control);

        VisualTreeHelper.GetChildrenCount(control).Should().Be(0);
    });

    private static TerminalControl NewControl() => new()
    {
        FontFamily = new FontFamily("Cascadia Mono, Consolas, Courier New"),
        FontSize = 14.0,
    };

    private static void ForceRender(TerminalControl control)
    {
        // Repaint runs in OnRender; the host hasn't put us on a real surface
        // so we drive it by calling InvalidateVisual then walking the visual
        // tree. The simplest deterministic path is to push through a
        // RenderTargetBitmap render, which fires OnRender as a side effect.
        var width = Math.Max((int)Math.Ceiling(control.DesiredSize.Width), 1);
        var height = Math.Max((int)Math.Ceiling(control.DesiredSize.Height), 1);
        control.Arrange(new Rect(0, 0, width, height));
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

    private static byte[] ReadPixels(BitmapSource bitmap, int x, int y, int width, int height)
    {
        width = Math.Min(width, bitmap.PixelWidth - x);
        height = Math.Min(height, bitmap.PixelHeight - y);
        var stride = width * 4;
        var buffer = new byte[stride * height];
        bitmap.CopyPixels(new Int32Rect(x, y, width, height), buffer, stride, 0);
        return buffer;
    }
}
