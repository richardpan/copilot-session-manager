using System.Windows;
using System.Windows.Media;
using FluentAssertions;

namespace CopilotSessionManager.Terminal.Wpf.Tests;

public class CellMetricsTests
{
    [Fact]
    public void Constructor_with_Cascadia_Mono_produces_positive_dimensions() => StaRunner.Run(() =>
    {
        var metrics = BuildMetricsForCascadiaMono(14.0);

        metrics.CellWidth.Should().BeGreaterThan(0);
        metrics.CellHeight.Should().BeGreaterThan(0);
        metrics.Baseline.Should().BeGreaterThan(0);
        metrics.Baseline.Should().BeLessThanOrEqualTo(metrics.CellHeight);
        metrics.EmSize.Should().Be(14.0);
        metrics.PixelsPerDip.Should().Be(1.0);
    });

    [Fact]
    public void Cell_width_scales_linearly_with_em_size() => StaRunner.Run(() =>
    {
        var small = BuildMetricsForCascadiaMono(10.0);
        var large = BuildMetricsForCascadiaMono(20.0);

        large.CellWidth.Should().BeApproximately(small.CellWidth * 2.0, 0.001);
        large.CellHeight.Should().BeApproximately(small.CellHeight * 2.0, 0.001);
    });

    [Fact]
    public void GlyphIndexFor_returns_consistent_value_for_M_and_space() => StaRunner.Run(() =>
    {
        var metrics = BuildMetricsForCascadiaMono(14.0);

        var m1 = metrics.GlyphIndexFor('M');
        var m2 = metrics.GlyphIndexFor('M');
        var space = metrics.GlyphIndexFor(' ');

        m1.Should().Be(m2);
        m1.Should().NotBe(space);
    });

    [Fact]
    public void GlyphIndexFor_unmapped_code_point_falls_back_to_space_glyph() => StaRunner.Run(() =>
    {
        var metrics = BuildMetricsForCascadiaMono(14.0);
        const int privateUseScalar = 0xE000;

        var fallback = metrics.GlyphIndexFor(privateUseScalar);
        var space = metrics.GlyphIndexFor(' ');

        fallback.Should().Be(space);
    });

    private static CellMetrics BuildMetricsForCascadiaMono(double emSize)
    {
        var typeface = new Typeface(
            new FontFamily("Cascadia Mono, Consolas, Courier New"),
            FontStyles.Normal,
            FontWeights.Normal,
            FontStretches.Normal);

        typeface.TryGetGlyphTypeface(out var glyphTypeface).Should().BeTrue();
        return new CellMetrics(glyphTypeface!, emSize, pixelsPerDip: 1.0);
    }
}
