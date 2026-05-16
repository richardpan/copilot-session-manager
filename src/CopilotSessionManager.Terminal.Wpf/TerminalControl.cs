using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using CopilotSessionManager.Terminal;

namespace CopilotSessionManager.Terminal.Wpf;

/// <summary>
/// Custom-drawn WPF host that renders a <see cref="ScreenBuffer"/> using
/// one <see cref="DrawingVisual"/> per terminal row, drawn with
/// <see cref="GlyphRun"/>s built from a cached <see cref="GlyphTypeface"/>.
/// Phase 3A of epic #93: bare control + full-viewport repaint on any
/// dependency-property change. Incremental dirty-row rendering arrives in
/// Phase 3B.
/// </summary>
/// <remarks>
/// The control derives from <see cref="FrameworkElement"/> rather than
/// <see cref="System.Windows.Controls.Control"/> because it owns its own
/// visual tree and has no use for theme templates.
/// </remarks>
public class TerminalControl : FrameworkElement
{
    /// <summary>The default monospace font family searched first.</summary>
    public const string DefaultFontFamilyName = "Cascadia Mono, Consolas, Courier New";

    /// <summary>The default em-size used until <see cref="FontSize"/> is set.</summary>
    public const double DefaultFontSize = 14.0;

    private readonly VisualCollection _children;
    private readonly List<DrawingVisual> _rowVisuals = new();

    private CellMetrics? _metrics;
    private int _renderedRows;

    /// <summary>Identifies the <see cref="Buffer"/> dependency property.</summary>
    public static readonly DependencyProperty BufferProperty = DependencyProperty.Register(
        nameof(Buffer),
        typeof(ScreenBuffer),
        typeof(TerminalControl),
        new FrameworkPropertyMetadata(
            defaultValue: null,
            FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender,
            propertyChangedCallback: OnVisualPropertyChanged));

    /// <summary>Identifies the <see cref="FontFamily"/> dependency property.</summary>
    public static readonly DependencyProperty FontFamilyProperty = DependencyProperty.Register(
        nameof(FontFamily),
        typeof(FontFamily),
        typeof(TerminalControl),
        new FrameworkPropertyMetadata(
            defaultValue: new FontFamily(DefaultFontFamilyName),
            FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender,
            propertyChangedCallback: OnMetricsPropertyChanged));

    /// <summary>Identifies the <see cref="FontSize"/> dependency property.</summary>
    public static readonly DependencyProperty FontSizeProperty = DependencyProperty.Register(
        nameof(FontSize),
        typeof(double),
        typeof(TerminalControl),
        new FrameworkPropertyMetadata(
            defaultValue: DefaultFontSize,
            FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender,
            propertyChangedCallback: OnMetricsPropertyChanged));

    /// <summary>Identifies the <see cref="Foreground"/> dependency property.</summary>
    public static readonly DependencyProperty ForegroundProperty = DependencyProperty.Register(
        nameof(Foreground),
        typeof(Color),
        typeof(TerminalControl),
        new FrameworkPropertyMetadata(
            defaultValue: Color.FromRgb(0xE5, 0xE5, 0xE5),
            FrameworkPropertyMetadataOptions.AffectsRender,
            propertyChangedCallback: OnVisualPropertyChanged));

    /// <summary>Identifies the <see cref="Background"/> dependency property.</summary>
    public static readonly DependencyProperty BackgroundProperty = DependencyProperty.Register(
        nameof(Background),
        typeof(Color),
        typeof(TerminalControl),
        new FrameworkPropertyMetadata(
            defaultValue: Color.FromRgb(0x12, 0x12, 0x12),
            FrameworkPropertyMetadataOptions.AffectsRender,
            propertyChangedCallback: OnVisualPropertyChanged));

    /// <summary>Construct a control with no buffer attached.</summary>
    public TerminalControl()
    {
        _children = new VisualCollection(this);
    }

    /// <summary>The screen buffer driving the renderer. Null until set.</summary>
    public ScreenBuffer? Buffer
    {
        get => (ScreenBuffer?)GetValue(BufferProperty);
        set => SetValue(BufferProperty, value);
    }

    /// <summary>Monospace font family. Falls back to the next entry if the first is missing.</summary>
    public FontFamily FontFamily
    {
        get => (FontFamily)GetValue(FontFamilyProperty);
        set => SetValue(FontFamilyProperty, value);
    }

    /// <summary>Font em-size in DIPs.</summary>
    public double FontSize
    {
        get => (double)GetValue(FontSizeProperty);
        set => SetValue(FontSizeProperty, value);
    }

    /// <summary>Colour used to draw glyphs whose cell foreground is <see cref="TerminalColor.Default"/>.</summary>
    public Color Foreground
    {
        get => (Color)GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    /// <summary>Colour used to draw cells whose background is <see cref="TerminalColor.Default"/>.</summary>
    public Color Background
    {
        get => (Color)GetValue(BackgroundProperty);
        set => SetValue(BackgroundProperty, value);
    }

    /// <summary>
    /// Cell metrics used by the most recent render pass. Null until the
    /// first render. Exposed for diagnostics and unit tests; not for
    /// general consumption.
    /// </summary>
    internal CellMetrics? Metrics => _metrics;

    /// <summary>Visual child count; one <see cref="DrawingVisual"/> per rendered terminal row.</summary>
    protected override int VisualChildrenCount => _children.Count;

    /// <inheritdoc />
    protected override Visual GetVisualChild(int index) => _children[index];

    /// <inheritdoc />
    protected override Size MeasureOverride(Size availableSize)
    {
        var buffer = Buffer;
        if (buffer is null)
        {
            return new Size(0, 0);
        }

        EnsureMetrics();
        var metrics = _metrics!;
        return new Size(
            buffer.Columns * metrics.CellWidth,
            buffer.Rows * metrics.CellHeight);
    }

    /// <inheritdoc />
    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        Repaint();
    }

    private static void OnVisualPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TerminalControl control)
        {
            control.Repaint();
        }
    }

    private static void OnMetricsPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TerminalControl control)
        {
            control._metrics = null;
            control.InvalidateMeasure();
            control.Repaint();
        }
    }

    private void EnsureMetrics()
    {
        if (_metrics is not null)
        {
            return;
        }

        var typeface = new Typeface(FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        if (!typeface.TryGetGlyphTypeface(out var glyphTypeface))
        {
            var fallback = new Typeface(
                new FontFamily("Consolas, Courier New, Global Monospace"),
                FontStyles.Normal,
                FontWeights.Normal,
                FontStretches.Normal);
            if (!fallback.TryGetGlyphTypeface(out glyphTypeface))
            {
                throw new InvalidOperationException(
                    "No monospace glyph typeface available for the terminal renderer.");
            }
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        var pixelsPerDip = dpi.PixelsPerDip > 0 ? dpi.PixelsPerDip : 1.0;
        _metrics = new CellMetrics(glyphTypeface, FontSize, pixelsPerDip);
    }

    private void Repaint()
    {
        var buffer = Buffer;
        if (buffer is null || buffer.Rows == 0 || buffer.Columns == 0)
        {
            ClearVisuals();
            return;
        }

        EnsureMetrics();
        var metrics = _metrics!;

        SyncVisualCount(buffer.Rows);

        for (var row = 1; row <= buffer.Rows; row++)
        {
            RenderRow(buffer, metrics, row, _rowVisuals[row - 1]);
        }
    }

    private void ClearVisuals()
    {
        _children.Clear();
        _rowVisuals.Clear();
        _renderedRows = 0;
    }

    private void SyncVisualCount(int rows)
    {
        if (rows == _renderedRows)
        {
            return;
        }

        while (_rowVisuals.Count < rows)
        {
            var visual = new DrawingVisual();
            _rowVisuals.Add(visual);
            _children.Add(visual);
        }

        while (_rowVisuals.Count > rows)
        {
            var idx = _rowVisuals.Count - 1;
            _children.Remove(_rowVisuals[idx]);
            _rowVisuals.RemoveAt(idx);
        }

        _renderedRows = rows;
    }

    private void RenderRow(ScreenBuffer buffer, CellMetrics metrics, int row, DrawingVisual visual)
    {
        using var dc = visual.RenderOpen();

        var defaultFg = Foreground;
        var defaultBg = Background;
        var y = (row - 1) * metrics.CellHeight;
        var columns = buffer.Columns;

        var col = 1;
        while (col <= columns)
        {
            var startCol = col;
            var anchor = buffer.GetCell(row, col);
            col++;
            while (col <= columns)
            {
                var c = buffer.GetCell(row, col);
                if (!ShareStyle(anchor, c))
                {
                    break;
                }
                col++;
            }
            var endCol = col - 1;
            var cellCount = endCol - startCol + 1;

            var (fg, bg) = ResolveStyleColors(anchor, defaultFg, defaultBg);

            var x = (startCol - 1) * metrics.CellWidth;
            var width = cellCount * metrics.CellWidth;

            if (bg != defaultBg)
            {
                var brush = new SolidColorBrush(bg);
                brush.Freeze();
                dc.DrawRectangle(brush, null, new Rect(x, y, width, metrics.CellHeight));
            }

            DrawGlyphsForRun(buffer, metrics, dc, row, startCol, cellCount, x, y, fg);
        }
    }

    private static bool ShareStyle(TerminalCell a, TerminalCell b) =>
        a.Foreground == b.Foreground
        && a.Background == b.Background
        && a.Attributes == b.Attributes;

    private static (Color fg, Color bg) ResolveStyleColors(TerminalCell cell, Color defaultFg, Color defaultBg)
    {
        var fg = TerminalPalette.Resolve(cell.Foreground, defaultFg);
        var bg = TerminalPalette.Resolve(cell.Background, defaultBg);
        if ((cell.Attributes & CellAttributes.Inverse) != 0)
        {
            (fg, bg) = (bg, fg);
        }
        return (fg, bg);
    }

    private void DrawGlyphsForRun(
        ScreenBuffer buffer,
        CellMetrics metrics,
        DrawingContext dc,
        int row,
        int startCol,
        int cellCount,
        double x,
        double y,
        Color fg)
    {
        var indices = new ushort[cellCount];
        var advances = new double[cellCount];
        var hasVisibleGlyph = false;

        for (var i = 0; i < cellCount; i++)
        {
            var cell = buffer.GetCell(row, startCol + i);
            var codePoint = cell.Glyph.Value;
            indices[i] = metrics.GlyphIndexFor(codePoint);
            advances[i] = metrics.CellWidth;
            if (codePoint != ' ')
            {
                hasVisibleGlyph = true;
            }
        }

        if (!hasVisibleGlyph)
        {
            return;
        }

        var origin = new Point(x, y + metrics.Baseline);
        var run = new GlyphRun(
            metrics.Typeface,
            bidiLevel: 0,
            isSideways: false,
            renderingEmSize: metrics.EmSize,
            pixelsPerDip: (float)metrics.PixelsPerDip,
            glyphIndices: indices,
            baselineOrigin: origin,
            advanceWidths: advances,
            glyphOffsets: null,
            characters: null,
            deviceFontName: null,
            clusterMap: null,
            caretStops: null,
            language: null);

        var brush = new SolidColorBrush(fg);
        brush.Freeze();
        dc.DrawGlyphRun(brush, run);
    }
}
