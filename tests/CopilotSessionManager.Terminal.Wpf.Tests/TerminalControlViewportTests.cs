using System.Windows;
using System.Windows.Media;
using CopilotSessionManager.Terminal;
using FluentAssertions;

namespace CopilotSessionManager.Terminal.Wpf.Tests;

public class TerminalControlViewportTests
{
    [Fact]
    public void CellsForViewport_floors_fractional_cell_counts() => StaRunner.Run(() =>
    {
        var control = ControlWithMetrics();
        var metrics = control.Metrics!;

        var cells = control.CellsForViewport(new Size(
            metrics.CellWidth * 10.75,
            metrics.CellHeight * 5.75));

        cells.Should().Be((5, 10));
    });

    [Fact]
    public void CellsForViewport_clamps_columns_when_width_is_below_one_cell() => StaRunner.Run(() =>
    {
        var control = ControlWithMetrics();
        var metrics = control.Metrics!;

        var cells = control.CellsForViewport(new Size(
            metrics.CellWidth * 0.5,
            metrics.CellHeight * 6.25));

        cells.Should().Be((6, 2));
    });

    [Fact]
    public void CellsForViewport_clamps_rows_when_height_is_below_one_cell() => StaRunner.Run(() =>
    {
        var control = ControlWithMetrics();
        var metrics = control.Metrics!;

        var cells = control.CellsForViewport(new Size(
            metrics.CellWidth * 7.25,
            metrics.CellHeight * 0.5));

        cells.Should().Be((2, 7));
    });

    [Fact]
    public void CellsForViewport_clamps_zero_pixel_size() => StaRunner.Run(() =>
    {
        var control = ControlWithMetrics();

        var cells = control.CellsForViewport(new Size(0, 0));

        cells.Should().Be((2, 2));
    });

    [Fact]
    public void CellsForViewport_before_first_render_returns_minimum_size() => StaRunner.Run(() =>
    {
        var control = NewControl();

        var cells = control.CellsForViewport(new Size(1000, 1000));

        cells.Should().Be((2, 2));
    });

    private static TerminalControl ControlWithMetrics()
    {
        var control = NewControl();
        control.Buffer = new ScreenBuffer(rows: 24, columns: 80);
        control.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        control.Metrics.Should().NotBeNull();
        return control;
    }

    private static TerminalControl NewControl() => new()
    {
        FontFamily = new FontFamily("Cascadia Mono, Consolas, Courier New"),
        FontSize = 14.0,
    };
}
