using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using CopilotSessionManager.Terminal;

namespace CopilotSessionManager.Terminal.Wpf;

/// <summary>
/// Custom-drawn WPF host that renders a <see cref="ScreenBuffer"/> using
/// one <see cref="DrawingVisual"/> per terminal row, drawn with
/// <see cref="GlyphRun"/>s built from a cached <see cref="GlyphTypeface"/>.
/// Phase 3B of epic #93: subscribes to
/// <see cref="ScreenBuffer.ViewportInvalidated"/>, coalesces incremental
/// repaints onto <see cref="DispatcherPriority.Render"/>, and only re-emits
/// drawing instructions for rows the buffer flagged dirty. A separate
/// cursor visual blinks at 500 ms intervals.
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

    /// <summary>Cursor blink interval (one full on+off cycle takes twice this).</summary>
    public static readonly TimeSpan CursorBlinkInterval = TimeSpan.FromMilliseconds(500);

    private readonly VisualCollection _children;
    private readonly List<DrawingVisual> _rowVisuals = new();
    private readonly DrawingVisual _cursorVisual = new();
    private readonly DispatcherTimer _cursorBlinkTimer;

    private CellMetrics? _metrics;
    private int _renderedRows;
    private bool _renderPending;
    private bool _cursorBlinkOn = true;
    private EventHandler? _bufferInvalidationHandler;

    /// <summary>Identifies the <see cref="Buffer"/> dependency property.</summary>
    public static readonly DependencyProperty BufferProperty = DependencyProperty.Register(
        nameof(Buffer),
        typeof(ScreenBuffer),
        typeof(TerminalControl),
        new FrameworkPropertyMetadata(
            defaultValue: null,
            FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender,
            propertyChangedCallback: OnBufferPropertyChanged));

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
            propertyChangedCallback: OnAppearancePropertyChanged));

    /// <summary>Identifies the <see cref="Background"/> dependency property.</summary>
    public static readonly DependencyProperty BackgroundProperty = DependencyProperty.Register(
        nameof(Background),
        typeof(Color),
        typeof(TerminalControl),
        new FrameworkPropertyMetadata(
            defaultValue: Color.FromRgb(0x12, 0x12, 0x12),
            FrameworkPropertyMetadataOptions.AffectsRender,
            propertyChangedCallback: OnAppearancePropertyChanged));

    /// <summary>Construct a control with no buffer attached.</summary>
    public TerminalControl()
    {
        _children = new VisualCollection(this);
        _cursorBlinkTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = CursorBlinkInterval,
        };
        _cursorBlinkTimer.Tick += OnCursorBlinkTick;
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
    /// first render. Exposed for diagnostics and unit tests.
    /// </summary>
    internal CellMetrics? Metrics => _metrics;

    /// <summary>True when the cursor visual is currently in its "on" blink phase.</summary>
    internal bool CursorBlinkOn => _cursorBlinkOn;

    /// <summary>The cursor visual, exposed for test inspection.</summary>
    internal DrawingVisual CursorVisual => _cursorVisual;

    /// <summary>Visual child count; one <see cref="DrawingVisual"/> per row plus the cursor visual.</summary>
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

        // When WPF re-renders (DP change with AffectsRender, initial
        // layout, or RenderTargetBitmap.Render in tests) we resync the
        // whole viewport. ViewportInvalidated handles deltas between
        // these synchronous resync points.
        FullRepaint();
    }

    /// <summary>
    /// Run any pending dispatcher-coalesced repaint synchronously. Used
    /// by tests to assert on rendered state after mutating the buffer
    /// without having to spin a real WPF message pump.
    /// </summary>
    internal void FlushPendingRender()
    {
        if (!_renderPending)
        {
            return;
        }
        _renderPending = false;
        IncrementalRepaint();
    }

    /// <summary>Advance the cursor blink one step. Exposed for unit tests.</summary>
    internal void AdvanceCursorBlinkForTest()
    {
        _cursorBlinkOn = !_cursorBlinkOn;
        UpdateCursorVisual();
    }

    private static void OnBufferPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TerminalControl control)
        {
            return;
        }

        if (e.OldValue is ScreenBuffer oldBuffer && control._bufferInvalidationHandler is not null)
        {
            oldBuffer.ViewportInvalidated -= control._bufferInvalidationHandler;
        }

        control._bufferInvalidationHandler = control.OnBufferViewportInvalidated;

        if (e.NewValue is ScreenBuffer newBuffer)
        {
            newBuffer.ViewportInvalidated += control._bufferInvalidationHandler;
            control._cursorBlinkOn = true;
            control._cursorBlinkTimer.Start();
        }
        else
        {
            control._cursorBlinkTimer.Stop();
        }

        control._renderedRows = 0;
        control.FullRepaint();
    }

    private static void OnMetricsPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TerminalControl control)
        {
            control._metrics = null;
            control.InvalidateMeasure();
            control._renderedRows = 0;
            control.FullRepaint();
        }
    }

    private static void OnAppearancePropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TerminalControl control)
        {
            control.FullRepaint();
        }
    }

    private void OnBufferViewportInvalidated(object? sender, EventArgs e)
    {
        var dispatcher = Dispatcher;
        if (dispatcher is null)
        {
            // No dispatcher means the control was never bound to a UI
            // thread (test-only scenario). Run inline.
            IncrementalRepaint();
            return;
        }

        if (_renderPending)
        {
            return;
        }
        _renderPending = true;

        if (dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
            {
                _renderPending = false;
                IncrementalRepaint();
            }));
        }
        else
        {
            dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
            {
                _renderPending = false;
                IncrementalRepaint();
            }));
        }
    }

    private void OnCursorBlinkTick(object? sender, EventArgs e)
    {
        if (Buffer is null)
        {
            return;
        }
        _cursorBlinkOn = !_cursorBlinkOn;
        UpdateCursorVisual();
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

    private void FullRepaint()
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

        buffer.ClearDirty();
        UpdateCursorVisual();
    }

    private void IncrementalRepaint()
    {
        var buffer = Buffer;
        if (buffer is null || buffer.Rows == 0 || buffer.Columns == 0)
        {
            ClearVisuals();
            return;
        }

        // A geometry change means the dirty-row map is stale — fall back
        // to a full repaint to keep the visual tree consistent with the
        // buffer.
        if (buffer.Rows != _renderedRows)
        {
            FullRepaint();
            return;
        }

        EnsureMetrics();
        var metrics = _metrics!;
        var dirty = buffer.DirtyRows;

        for (var row = 1; row <= buffer.Rows; row++)
        {
            if (!dirty[row - 1])
            {
                continue;
            }
            RenderRow(buffer, metrics, row, _rowVisuals[row - 1]);
        }

        buffer.ClearDirty();
        UpdateCursorVisual();
    }

    private void ClearVisuals()
    {
        _children.Clear();
        _rowVisuals.Clear();
        _renderedRows = 0;
        using (_cursorVisual.RenderOpen())
        {
            // Drop any cached cursor content.
        }
    }

    private void SyncVisualCount(int rows)
    {
        if (rows == _renderedRows && _children.Contains(_cursorVisual))
        {
            return;
        }

        // Detach the cursor while we rebuild row visuals; re-attach last
        // so it stays the top-most child.
        if (_children.Contains(_cursorVisual))
        {
            _children.Remove(_cursorVisual);
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

        _children.Add(_cursorVisual);
        _renderedRows = rows;
    }

    private void UpdateCursorVisual()
    {
        var buffer = Buffer;
        using var dc = _cursorVisual.RenderOpen();

        if (buffer is null || _metrics is null || !buffer.CursorVisible || !_cursorBlinkOn)
        {
            return;
        }

        var metrics = _metrics;
        var x = (buffer.CursorColumn - 1) * metrics.CellWidth;
        var y = (buffer.CursorRow - 1) * metrics.CellHeight;
        var rect = new Rect(x, y, metrics.CellWidth, metrics.CellHeight);

        var brush = new SolidColorBrush(Foreground);
        brush.Freeze();
        dc.DrawRectangle(brush, null, rect);
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
