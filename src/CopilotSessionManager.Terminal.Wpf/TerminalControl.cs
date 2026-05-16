using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
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
    private readonly DrawingVisual _selectionVisual = new();
    private readonly DrawingVisual _cursorVisual = new();
    private readonly DispatcherTimer _cursorBlinkTimer;

    private CellMetrics? _metrics;
    private int _renderedRows;
    private bool _renderPending;
    private bool _cursorBlinkOn = true;
    private bool _suppressNextTextInput;
    private bool _selecting;
    private TerminalSelection? _selection;
    private ITerminalClipboard _clipboard = new WpfClipboard();
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

    /// <summary>Identifies the <see cref="SelectionBrush"/> dependency property.</summary>
    public static readonly DependencyProperty SelectionBrushProperty = DependencyProperty.Register(
        nameof(SelectionBrush),
        typeof(Color),
        typeof(TerminalControl),
        new FrameworkPropertyMetadata(
            defaultValue: Color.FromArgb(0x66, 0x33, 0x88, 0xCC),
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

        // The control needs keyboard focus to receive key/text events,
        // and we suppress the focus rectangle because the cursor visual
        // is the user's focus affordance.
        Focusable = true;
        FocusVisualStyle = null;
    }

    /// <summary>
    /// Raised on the WPF dispatcher thread whenever the user produces
    /// bytes — special keys, text input, or <see cref="Paste"/> — that
    /// should be forwarded to the PTY input stream.
    /// </summary>
    public event EventHandler<TerminalInputEventArgs>? InputProduced;

    /// <summary>
    /// When <c>true</c>, cursor keys, Home and End emit the DECCKM
    /// "application" sequences (<c>ESC O A</c> etc.) instead of the
    /// normal-mode CSI sequences. Defaults to <c>false</c>; a future PR
    /// will wire this to the parser's mode state.
    /// </summary>
    public bool UseApplicationCursorKeys { get; set; }

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

    /// <summary>Overlay colour used to highlight selected cells. Should be semi-transparent.</summary>
    public Color SelectionBrush
    {
        get => (Color)GetValue(SelectionBrushProperty);
        set => SetValue(SelectionBrushProperty, value);
    }

    /// <summary>The current text selection, or <c>null</c> if nothing is selected.</summary>
    public TerminalSelection? Selection => _selection;

    /// <summary>Raised whenever <see cref="Selection"/> changes value (including transitions to or from null).</summary>
    public event EventHandler? SelectionChanged;

    /// <summary>
    /// The clipboard abstraction used by <see cref="CopyToClipboard"/>
    /// and <see cref="PasteFromClipboard"/>. Defaults to a
    /// <see cref="WpfClipboard"/> wrapping <see cref="System.Windows.Clipboard"/>;
    /// tests inject a fake.
    /// </summary>
    public ITerminalClipboard Clipboard
    {
        get => _clipboard;
        set => _clipboard = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>
    /// Cell metrics used by the most recent render pass. Null until the
    /// first render. Exposed for diagnostics and unit tests.
    /// </summary>
    internal CellMetrics? Metrics => _metrics;

    /// <summary>
    /// Convert a pixel viewport size to terminal rows and columns using the
    /// current cell metrics, clamped to the minimum pseudo-console size.
    /// </summary>
    public (int Rows, int Cols) CellsForViewport(Size pixelSize)
    {
        var metrics = _metrics;
        if (metrics is null)
        {
            return (2, 2);
        }

        return (
            CellsForAxis(pixelSize.Height, metrics.CellHeight),
            CellsForAxis(pixelSize.Width, metrics.CellWidth));
    }

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

    /// <inheritdoc />
    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonDown(e);

        if (Focusable && !IsKeyboardFocused)
        {
            Focus();
        }

        var cell = HitTestCell(e.GetPosition(this));
        if (cell is null)
        {
            return;
        }
        BeginSelection(cell.Value.Row, cell.Value.Column);
        CaptureMouse();
        e.Handled = true;
    }

    /// <inheritdoc />
    protected override void OnPreviewMouseMove(MouseEventArgs e)
    {
        base.OnPreviewMouseMove(e);
        if (!_selecting || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }
        var cell = HitTestCell(e.GetPosition(this));
        if (cell is null)
        {
            return;
        }
        UpdateSelection(cell.Value.Row, cell.Value.Column);
    }

    /// <inheritdoc />
    protected override void OnPreviewMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonUp(e);
        if (!_selecting)
        {
            return;
        }
        EndSelection();
        if (IsMouseCaptured)
        {
            ReleaseMouseCapture();
        }
        e.Handled = true;
    }

    /// <inheritdoc />
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Handled)
        {
            return;
        }

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (DispatchKeyCore(key, Keyboard.Modifiers))
        {
            e.Handled = true;
        }
    }

    /// <inheritdoc />
    protected override void OnTextInput(TextCompositionEventArgs e)
    {
        base.OnTextInput(e);

        if (DispatchTextInputCore(e.Text, Keyboard.Modifiers))
        {
            e.Handled = true;
        }
    }

    /// <summary>
    /// Write <paramref name="text"/> to the PTY input stream, wrapping it
    /// in bracketed-paste delimiters when the buffer has DEC private mode
    /// 2004 enabled. Safe to call from any thread that has access to the
    /// WPF dispatcher; the event is raised inline.
    /// </summary>
    public void Paste(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }
        var bracketed = Buffer?.BracketedPasteEnabled ?? false;
        var bytes = VtKeyEncoder.EncodePaste(text, bracketed);
        EmitInput(bytes);
    }

    /// <summary>
    /// Test hook: invoke the key-dispatch path without going through the
    /// WPF input pipeline. Returns <c>true</c> when input bytes were
    /// produced.
    /// </summary>
    internal bool DispatchKeyForTest(Key key, ModifierKeys modifiers)
        => DispatchKeyCore(key, modifiers);

    /// <summary>
    /// Test hook: invoke the text-input dispatch path without WPF event
    /// plumbing. Returns <c>true</c> when input bytes were produced.
    /// </summary>
    internal bool DispatchTextInputForTest(string text, ModifierKeys modifiers)
        => DispatchTextInputCore(text, modifiers);

    private bool DispatchKeyCore(Key key, ModifierKeys wpfModifiers)
    {
        var modifiers = MapModifiers(wpfModifiers);

        // Clipboard shortcuts intercept the key before any byte emission.
        // Ctrl+C copies when there is a selection, otherwise falls through
        // to the encoder so the shell still receives SIGINT (0x03).
        // Ctrl+V always pastes from the clipboard.
        if (modifiers == TerminalKeyModifiers.Control)
        {
            if (key == Key.C && _selection is not null && !_selection.IsEmpty)
            {
                CopyToClipboard();
                ClearSelection();
                return true;
            }
            if (key == Key.V)
            {
                PasteFromClipboard();
                ClearSelection();
                return true;
            }
        }

        var tkey = MapKey(key);
        if (tkey != TerminalKey.None)
        {
            var bytes = VtKeyEncoder.Encode(tkey, modifiers, UseApplicationCursorKeys);
            if (bytes is not null && bytes.Length > 0)
            {
                EmitInput(bytes);
                ClearSelection();
                if (tkey is TerminalKey.Enter or TerminalKey.Tab or TerminalKey.Backspace or TerminalKey.Escape)
                {
                    _suppressNextTextInput = true;
                }
                return true;
            }
        }

        var ctrl = (modifiers & TerminalKeyModifiers.Control) != 0;
        var alt = (modifiers & TerminalKeyModifiers.Alt) != 0;
        if (!ctrl && !alt)
        {
            return false;
        }

        var ch = TryGetCharFromKey(key);
        if (ch is null)
        {
            return false;
        }

        byte[]? bytes2 = null;
        if (ctrl)
        {
            bytes2 = VtKeyEncoder.EncodeControlChar(ch.Value);
            if (bytes2 is not null && alt)
            {
                var prefixed = new byte[bytes2.Length + 1];
                prefixed[0] = 0x1B;
                Array.Copy(bytes2, 0, prefixed, 1, bytes2.Length);
                bytes2 = prefixed;
            }
        }
        else if (alt)
        {
            bytes2 = VtKeyEncoder.EncodeText(ch.Value.ToString(), altHeld: true);
        }

        if (bytes2 is null || bytes2.Length == 0)
        {
            return false;
        }
        EmitInput(bytes2);
        ClearSelection();
        _suppressNextTextInput = true;
        return true;
    }

    private bool DispatchTextInputCore(string? text, ModifierKeys wpfModifiers)
    {
        if (_suppressNextTextInput)
        {
            _suppressNextTextInput = false;
            return true;
        }

        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        var modifiers = MapModifiers(wpfModifiers);
        var altHeld = (modifiers & TerminalKeyModifiers.Alt) != 0;
        var bytes = VtKeyEncoder.EncodeText(text, altHeld);
        if (bytes.Length == 0)
        {
            return false;
        }
        EmitInput(bytes);
        ClearSelection();
        return true;
    }


    /// <summary>
    /// Begin a fresh selection anchored at the given 1-based cell. The
    /// focus starts equal to the anchor (empty selection).
    /// </summary>
    public void BeginSelection(int row, int column)
    {
        var buffer = Buffer;
        if (buffer is null)
        {
            return;
        }
        row = Math.Clamp(row, 1, buffer.Rows);
        column = Math.Clamp(column, 1, buffer.Columns);
        _selecting = true;
        SetSelection(new TerminalSelection(row, column, row, column));
    }

    /// <summary>Update the focus end of an in-progress selection.</summary>
    public void UpdateSelection(int row, int column)
    {
        if (_selection is null)
        {
            return;
        }
        var buffer = Buffer;
        if (buffer is null)
        {
            return;
        }
        row = Math.Clamp(row, 1, buffer.Rows);
        column = Math.Clamp(column, 1, buffer.Columns);
        SetSelection(_selection with { FocusRow = row, FocusColumn = column });
    }

    /// <summary>Finalize the selection (mouse-up). Keeps the selection visible.</summary>
    public void EndSelection() => _selecting = false;

    /// <summary>Clear any active selection.</summary>
    public void ClearSelection()
    {
        _selecting = false;
        if (_selection is null)
        {
            return;
        }
        SetSelection(null);
    }

    /// <summary>Return the text spanned by <see cref="Selection"/>, or empty when there is none.</summary>
    public string GetSelectedText()
    {
        var sel = _selection;
        var buffer = Buffer;
        if (sel is null || sel.IsEmpty || buffer is null)
        {
            return string.Empty;
        }
        return SelectionTextExtractor.Extract(buffer, sel);
    }

    /// <summary>
    /// Copy <see cref="GetSelectedText"/> to <see cref="Clipboard"/>. No-op
    /// when the selection is empty.
    /// </summary>
    public void CopyToClipboard()
    {
        var text = GetSelectedText();
        if (text.Length == 0)
        {
            return;
        }
        _clipboard.SetText(text);
    }

    /// <summary>Read text from <see cref="Clipboard"/> and feed it through <see cref="Paste"/>.</summary>
    public void PasteFromClipboard()
    {
        var text = _clipboard.GetText();
        if (string.IsNullOrEmpty(text))
        {
            return;
        }
        Paste(text);
    }

    /// <summary>Test hook: drive a "mouse down at cell" event without WPF mouse plumbing.</summary>
    internal void DispatchMouseDownForTest(int row, int column)
    {
        BeginSelection(row, column);
    }

    /// <summary>Test hook: drive a "mouse drag to cell" event.</summary>
    internal void DispatchMouseDragForTest(int row, int column)
    {
        UpdateSelection(row, column);
    }

    /// <summary>Test hook: drive a "mouse up" event.</summary>
    internal void DispatchMouseUpForTest() => EndSelection();

    /// <summary>Selection visual exposed for test inspection.</summary>
    internal DrawingVisual SelectionVisual => _selectionVisual;

    private void SetSelection(TerminalSelection? selection)
    {
        if (Equals(_selection, selection))
        {
            return;
        }
        _selection = selection;
        UpdateSelectionVisual();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private (int Row, int Column)? HitTestCell(Point position)
    {
        var buffer = Buffer;
        if (buffer is null)
        {
            return null;
        }
        EnsureMetrics();
        var metrics = _metrics!;
        if (metrics.CellWidth <= 0 || metrics.CellHeight <= 0)
        {
            return null;
        }
        var col = (int)Math.Floor(position.X / metrics.CellWidth) + 1;
        var row = (int)Math.Floor(position.Y / metrics.CellHeight) + 1;
        col = Math.Clamp(col, 1, buffer.Columns);
        row = Math.Clamp(row, 1, buffer.Rows);
        return (row, col);
    }

    private static int CellsForAxis(double pixels, double cellSize)
    {
        if (!double.IsFinite(pixels) || !double.IsFinite(cellSize) || pixels <= 0 || cellSize <= 0)
        {
            return 2;
        }

        return Math.Max(2, (int)Math.Floor(pixels / cellSize));
    }

    private void EmitInput(byte[] bytes)
    {
        if (bytes is null || bytes.Length == 0)
        {
            return;
        }
        InputProduced?.Invoke(this, new TerminalInputEventArgs(bytes));
    }

    private static TerminalKeyModifiers MapModifiers(ModifierKeys mods)
    {
        var result = TerminalKeyModifiers.None;
        if ((mods & ModifierKeys.Shift) != 0)
        {
            result |= TerminalKeyModifiers.Shift;
        }
        if ((mods & ModifierKeys.Alt) != 0)
        {
            result |= TerminalKeyModifiers.Alt;
        }
        if ((mods & ModifierKeys.Control) != 0)
        {
            result |= TerminalKeyModifiers.Control;
        }
        return result;
    }

    private static TerminalKey MapKey(Key key) => key switch
    {
        Key.Up => TerminalKey.Up,
        Key.Down => TerminalKey.Down,
        Key.Left => TerminalKey.Left,
        Key.Right => TerminalKey.Right,
        Key.Home => TerminalKey.Home,
        Key.End => TerminalKey.End,
        Key.PageUp => TerminalKey.PageUp,
        Key.PageDown => TerminalKey.PageDown,
        Key.Insert => TerminalKey.Insert,
        Key.Delete => TerminalKey.Delete,
        Key.Tab => TerminalKey.Tab,
        Key.Enter => TerminalKey.Enter,
        Key.Back => TerminalKey.Backspace,
        Key.Escape => TerminalKey.Escape,
        Key.F1 => TerminalKey.F1,
        Key.F2 => TerminalKey.F2,
        Key.F3 => TerminalKey.F3,
        Key.F4 => TerminalKey.F4,
        Key.F5 => TerminalKey.F5,
        Key.F6 => TerminalKey.F6,
        Key.F7 => TerminalKey.F7,
        Key.F8 => TerminalKey.F8,
        Key.F9 => TerminalKey.F9,
        Key.F10 => TerminalKey.F10,
        Key.F11 => TerminalKey.F11,
        Key.F12 => TerminalKey.F12,
        _ => TerminalKey.None,
    };

    private static char? TryGetCharFromKey(Key key)
    {
        if (key >= Key.A && key <= Key.Z)
        {
            return (char)('a' + (key - Key.A));
        }
        if (key >= Key.D0 && key <= Key.D9)
        {
            return (char)('0' + (key - Key.D0));
        }
        if (key >= Key.NumPad0 && key <= Key.NumPad9)
        {
            return (char)('0' + (key - Key.NumPad0));
        }
        return key switch
        {
            Key.Space => ' ',
            Key.OemOpenBrackets => '[',
            Key.OemCloseBrackets => ']',
            Key.OemBackslash => '\\',
            Key.Oem5 => '\\',
            Key.OemMinus => '-',
            Key.OemPlus => '=',
            Key.OemSemicolon => ';',
            Key.OemQuotes => '\'',
            Key.OemComma => ',',
            Key.OemPeriod => '.',
            Key.OemQuestion => '/',
            Key.OemTilde => '`',
            _ => null,
        };
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
        UpdateSelectionVisual();
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
        UpdateSelectionVisual();
        UpdateCursorVisual();
    }

    private void ClearVisuals()
    {
        _children.Clear();
        _rowVisuals.Clear();
        _renderedRows = 0;
        using (_selectionVisual.RenderOpen())
        {
            // Drop any cached selection overlay.
        }
        using (_cursorVisual.RenderOpen())
        {
            // Drop any cached cursor content.
        }
    }

    private void SyncVisualCount(int rows)
    {
        if (rows == _renderedRows
            && _children.Contains(_selectionVisual)
            && _children.Contains(_cursorVisual))
        {
            return;
        }

        // Detach the overlays while we rebuild row visuals; re-attach in
        // z-order: rows, then selection (over rows), then cursor (over
        // selection).
        if (_children.Contains(_cursorVisual))
        {
            _children.Remove(_cursorVisual);
        }
        if (_children.Contains(_selectionVisual))
        {
            _children.Remove(_selectionVisual);
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

        _children.Add(_selectionVisual);
        _children.Add(_cursorVisual);
        _renderedRows = rows;
    }

    private void UpdateSelectionVisual()
    {
        var buffer = Buffer;
        using var dc = _selectionVisual.RenderOpen();

        var sel = _selection;
        if (sel is null || sel.IsEmpty || buffer is null || _metrics is null)
        {
            return;
        }

        var metrics = _metrics;
        var norm = sel.Normalize();
        var startRow = Math.Clamp(norm.AnchorRow, 1, buffer.Rows);
        var endRow = Math.Clamp(norm.FocusRow, 1, buffer.Rows);
        var startCol = Math.Clamp(norm.AnchorColumn, 1, buffer.Columns);
        var endCol = Math.Clamp(norm.FocusColumn, 1, buffer.Columns);

        var brush = new SolidColorBrush(SelectionBrush);
        brush.Freeze();

        for (var row = startRow; row <= endRow; row++)
        {
            var first = row == startRow ? startCol : 1;
            var last = row == endRow ? endCol : buffer.Columns;
            var x = (first - 1) * metrics.CellWidth;
            var y = (row - 1) * metrics.CellHeight;
            var width = (last - first + 1) * metrics.CellWidth;
            dc.DrawRectangle(brush, null, new Rect(x, y, width, metrics.CellHeight));
        }
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
