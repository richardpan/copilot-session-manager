using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
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
    private readonly MenuItem _copyMenuItem;
    private readonly MenuItem _pasteMenuItem;

    private CellMetrics? _metrics;
    private int _renderedRows;
    private int _renderedCols;
    private bool _renderPending;
    private bool _cursorBlinkOn = true;
    private bool _suppressNextTextInput;
    private bool _selecting;
    private TerminalSelection? _selection;
    private ITerminalClipboard _clipboard = new WpfClipboard();
    private EventHandler? _bufferInvalidationHandler;
    private EventHandler? _bufferAppCursorKeysHandler;

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

        // Disable the WPF InputMethod (IME / TSF). Terminal emulators
        // handle their own text input via VT sequences; the managed text
        // services pipeline can swallow the first TextInput event after a
        // focus change (the TSF context is lazily initialised per-element,
        // and the first keystroke can be consumed during that handshake).
        // Disabling the InputMethod avoids this race entirely and is the
        // standard practice for custom terminal controls in WPF.
        InputMethod.SetIsInputMethodEnabled(this, false);

        // Issue #180: right-click context menu with Copy and Paste.
        // Enabled state is refreshed on ContextMenuOpening so the menu
        // reflects the current selection and clipboard contents.
        _copyMenuItem = new MenuItem { Header = "Copy" };
        _copyMenuItem.Click += (_, _) => CopyToClipboard();
        _pasteMenuItem = new MenuItem { Header = "Paste" };
        _pasteMenuItem.Click += (_, _) => PasteFromClipboard();

        var menu = new ContextMenu();
        menu.Items.Add(_copyMenuItem);
        menu.Items.Add(_pasteMenuItem);
        ContextMenu = menu;
        ContextMenuOpening += (_, _) => RefreshContextMenuEnabledState();
        RefreshContextMenuEnabledState();
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
    /// normal-mode CSI sequences. Defaults to <c>false</c>. When a
    /// <see cref="Buffer"/> is attached the control mirrors the
    /// buffer's <c>ApplicationCursorKeys</c> here automatically so
    /// PSReadLine / vim / etc. round-trip correctly when they flip
    /// DECCKM on init (issue #177). Manual setters still work for
    /// callers that drive the control without a buffer.
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

        // The desired size is the buffer's cell grid, but when the parent
        // offers more space (Stretch alignment inside a Grid/Border), we
        // accept it so the control fills the container. This lets the
        // resize-feedback loop (SizeChanged → PTY resize → buffer grows →
        // repaint) work correctly instead of clamping the control to the
        // buffer's initial 30×100 default.
        var bufferWidth  = buffer.Columns * metrics.CellWidth;
        var bufferHeight = buffer.Rows    * metrics.CellHeight;

        var width  = double.IsInfinity(availableSize.Width)
            ? bufferWidth
            : Math.Max(bufferWidth, availableSize.Width);
        var height = double.IsInfinity(availableSize.Height)
            ? bufferHeight
            : Math.Max(bufferHeight, availableSize.Height);

        return new Size(width, height);
    }

    /// <inheritdoc />
    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);

        // Fill the entire control area with the background colour so the
        // element itself is hit-testable. Without this fill, areas not
        // covered by row DrawingVisuals (e.g. the strip below the last
        // rendered row) are transparent to WPF input hit testing, and
        // clicks there fall through to the parent Border — the control
        // never receives focus and keyboard input is lost.
        if (RenderSize.Width > 0 && RenderSize.Height > 0)
        {
            var brush = new SolidColorBrush(Background);
            brush.Freeze();
            drawingContext.DrawRectangle(brush, null, new Rect(RenderSize));
        }

        // When WPF re-renders (DP change with AffectsRender, initial
        // layout, or RenderTargetBitmap.Render in tests) we resync the
        // whole viewport. ViewportInvalidated handles deltas between
        // these synchronous resync points.
        FullRepaint();
    }

    /// <summary>
    /// Always return a positive hit for any point within the control's
    /// layout bounds. The default <c>UIElement.HitTestCore</c>
    /// only returns a hit when <see cref="OnRender"/> has drawn content at
    /// that point. Because this control renders its terminal rows into
    /// child <see cref="DrawingVisual"/> objects (not the element's own
    /// drawing context), empty-row areas — where the row visual has
    /// nothing drawn (default background, spaces only) — would otherwise
    /// be invisible to WPF input hit testing. That caused clicks on empty
    /// terminal space to target the parent <c>Border</c> instead of this
    /// control, preventing keyboard focus from being set.
    /// </summary>
    protected override HitTestResult? HitTestCore(PointHitTestParameters hitTestParameters)
    {
        // Accept any point within the arranged bounds so the entire
        // terminal surface is interactive, regardless of what content the
        // child DrawingVisuals have rendered.
        var pt = hitTestParameters.HitPoint;
        if (pt.X >= 0 && pt.Y >= 0
            && pt.X <= RenderSize.Width && pt.Y <= RenderSize.Height)
        {
            return new PointHitTestResult(this, pt);
        }
        return null;
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

        // Issue #178: triple-click selects the row, double-click
        // selects the word under the cursor, Shift+click extends the
        // current selection (anchor sticks, focus moves). Bare click
        // falls through to the prior begin-selection behaviour.
        // Issue #179: bare Alt+click begins a rectangular (block)
        // selection that drag extends per cell. Shift wins over Alt
        // (extending an existing selection keeps its mode).
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
        var alt = (Keyboard.Modifiers & ModifierKeys.Alt) == ModifierKeys.Alt;
        if (e.ClickCount >= 3)
        {
            SelectRow(cell.Value.Row);
        }
        else if (e.ClickCount == 2)
        {
            SelectWord(cell.Value.Row, cell.Value.Column);
        }
        else if (shift)
        {
            ExtendSelectionTo(cell.Value.Row, cell.Value.Column);
        }
        else if (alt)
        {
            BeginSelection(cell.Value.Row, cell.Value.Column, SelectionMode.Rectangle);
        }
        else
        {
            BeginSelection(cell.Value.Row, cell.Value.Column);
        }
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
            return;
        }

        // DispatchKeyCore returned false — the key isn't a special key or
        // Ctrl/Alt chord. Try to convert it to a printable character
        // directly via Win32 ToUnicode, bypassing the WPF TextInput
        // pipeline entirely. This is the standard approach for terminal
        // emulators: WPF's TextInput path can swallow the first character
        // after a focus change (TSF initialisation, AccessKeyManager
        // interception, or TabControl focus-steal races), and going
        // through OnPreviewKeyDown → ToUnicode avoids all of those.
        if (TryConvertKeyToChar(e, out var ch))
        {
            var bytes = VtKeyEncoder.EncodeText(ch.ToString(), altHeld: false);
            if (bytes.Length > 0)
            {
                EmitInput(bytes);
                ClearSelection();
                _suppressNextTextInput = true;
                e.Handled = true;
            }
        }
    }

    /// <summary>
    /// Handle text input during the tunneling (Preview) phase so the
    /// character is consumed before any ancestor (Menu, AccessKeyManager,
    /// TabControl) or the TSF post-processing pipeline can intercept it.
    /// The bubbling <see cref="OnTextInput"/> is kept as a no-op fallback
    /// — in practice <c>e.Handled = true</c> here prevents the bubbling
    /// event from firing.
    /// </summary>
    protected override void OnPreviewTextInput(TextCompositionEventArgs e)
    {
        base.OnPreviewTextInput(e);

        if (DispatchTextInputCore(e.Text, Keyboard.Modifiers))
        {
            e.Handled = true;
        }
    }

    /// <inheritdoc />
    protected override void OnTextInput(TextCompositionEventArgs e)
    {
        base.OnTextInput(e);

        // Normally reached only when OnPreviewTextInput did not mark the
        // event as handled (e.g. a future code path that returns false).
        if (!e.Handled && DispatchTextInputCore(e.Text, Keyboard.Modifiers))
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
    /// focus starts equal to the anchor (empty selection). Equivalent to
    /// <see cref="BeginSelection(int, int, SelectionMode)"/> with
    /// <see cref="SelectionMode.Stream"/>.
    /// </summary>
    public void BeginSelection(int row, int column) => BeginSelection(row, column, SelectionMode.Stream);

    /// <summary>
    /// Issue #179: begin a fresh selection anchored at the given 1-based
    /// cell with an explicit <paramref name="mode"/>. The focus starts
    /// equal to the anchor; subsequent <see cref="UpdateSelection(int,
    /// int)"/> calls preserve the mode via record-with semantics.
    /// </summary>
    public void BeginSelection(int row, int column, SelectionMode mode)
    {
        var buffer = Buffer;
        if (buffer is null)
        {
            return;
        }
        row = Math.Clamp(row, 1, buffer.Rows);
        column = Math.Clamp(column, 1, buffer.Columns);
        _selecting = true;
        SetSelection(new TerminalSelection(row, column, row, column) { Mode = mode });
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

    /// <summary>
    /// Issue #178: Shift+click. Extend the current selection so its
    /// focus is at (<paramref name="row"/>, <paramref name="column"/>),
    /// keeping the existing anchor in place. If no selection exists,
    /// this behaves like <see cref="BeginSelection(int, int)"/> at the supplied
    /// cell. Enters drag-extend mode so a subsequent mouse-move
    /// continues to move the focus.
    /// </summary>
    public void ExtendSelectionTo(int row, int column)
    {
        var buffer = Buffer;
        if (buffer is null)
        {
            return;
        }
        row = Math.Clamp(row, 1, buffer.Rows);
        column = Math.Clamp(column, 1, buffer.Columns);

        if (_selection is null)
        {
            BeginSelection(row, column);
            return;
        }

        _selecting = true;
        SetSelection(_selection with { FocusRow = row, FocusColumn = column });
    }

    /// <summary>
    /// Issue #178: double-click. Select the maximal whitespace-bounded
    /// run of cells covering (<paramref name="row"/>,
    /// <paramref name="column"/>). Whitespace under the cursor yields a
    /// single-cell selection. Selection stays "live" so the user can
    /// drag to extend it word-by-word? No, drag just extends per cell;
    /// that's good enough for the common case.
    /// </summary>
    public void SelectWord(int row, int column)
    {
        var buffer = Buffer;
        if (buffer is null)
        {
            return;
        }
        row = Math.Clamp(row, 1, buffer.Rows);
        column = Math.Clamp(column, 1, buffer.Columns);

        var (start, end) = WordBoundaryFinder.FindWord(buffer, row, column);
        _selecting = true;
        SetSelection(new TerminalSelection(row, start, row, end));
    }

    /// <summary>
    /// Issue #178: triple-click. Select the entire <paramref name="row"/>
    /// from column 1 through the buffer's last column. Selection stays
    /// live so drag continues to extend per cell.
    /// </summary>
    public void SelectRow(int row)
    {
        var buffer = Buffer;
        if (buffer is null)
        {
            return;
        }
        row = Math.Clamp(row, 1, buffer.Rows);
        _selecting = true;
        SetSelection(new TerminalSelection(row, 1, row, buffer.Columns));
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

    /// <summary>
    /// Issue #180: Refresh the IsEnabled state of the right-click context
    /// menu's Copy and Paste entries. Copy is enabled iff a non-empty
    /// selection exists; Paste is enabled iff the clipboard reports
    /// non-empty text. Called automatically on ContextMenuOpening; tests
    /// may invoke directly to bypass WPF event plumbing.
    /// </summary>
    public void RefreshContextMenuEnabledState()
    {
        _copyMenuItem.IsEnabled = _selection is not null && !_selection.IsEmpty;
        _pasteMenuItem.IsEnabled = !string.IsNullOrEmpty(_clipboard.GetText());
    }

    /// <summary>Test hook: the Copy entry on the right-click context menu.</summary>
    internal MenuItem CopyMenuItem => _copyMenuItem;

    /// <summary>Test hook: the Paste entry on the right-click context menu.</summary>
    internal MenuItem PasteMenuItem => _pasteMenuItem;

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
    /// Convert a WPF key event to a printable character using the Win32
    /// <c>ToUnicode</c> API. This respects the active keyboard layout,
    /// Shift, CapsLock, and NumLock — unlike <see cref="TryGetCharFromKey"/>
    /// which only handles US-QWERTY lowercase. Returns <c>false</c> for
    /// dead keys, non-printable keys, or keys already handled by
    /// <see cref="DispatchKeyCore"/> (Ctrl/Alt chords, special keys).
    /// </summary>
    private static bool TryConvertKeyToChar(KeyEventArgs e, out char result)
    {
        result = default;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        // Don't convert modifier keys themselves.
        if (key is Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl
                or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin
                or Key.CapsLock or Key.NumLock or Key.Scroll)
        {
            return false;
        }

        // Don't convert if Ctrl or Alt is held — those combos are already
        // handled in DispatchKeyCore, and ToUnicode would produce control
        // characters (0x01–0x1A) that we don't want here.
        var mods = Keyboard.Modifiers;
        if ((mods & ModifierKeys.Control) != 0 || (mods & ModifierKeys.Alt) != 0)
        {
            return false;
        }

        var virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);
        var scanCode = (uint)NativeKeyboard.MapVirtualKey(virtualKey, NativeKeyboard.MAPVK_VK_TO_VSC);

        // Snapshot the 256-byte keyboard state (Shift, CapsLock, etc.)
        var keyboardState = new byte[256];
        if (!NativeKeyboard.GetKeyboardState(keyboardState))
        {
            return false;
        }

        var chars = new char[2];
        var count = NativeKeyboard.ToUnicode(
            virtualKey, scanCode, keyboardState,
            chars, chars.Length, 0);

        if (count == 1)
        {
            var ch = chars[0];
            // Filter out control characters (Tab, Enter, etc. produce
            // 0x09, 0x0D which we don't want here; they go through
            // DispatchKeyCore via MapKey).
            if (!char.IsControl(ch))
            {
                result = ch;
                return true;
            }
        }
        // count == -1 → dead key (accent); count == 0 → no mapping.
        // Both are fine: fall through to the TextInput pipeline.
        return false;
    }

    /// <summary>
    /// P/Invoke declarations for <c>user32.dll</c> keyboard functions
    /// used by <see cref="TryConvertKeyToChar"/>. Kept as a private nested
    /// class so the terminal control stays self-contained.
    /// </summary>
    private static class NativeKeyboard
    {
        public const uint MAPVK_VK_TO_VSC = 0;

        [DllImport("user32.dll")]
        public static extern uint MapVirtualKey(uint uCode, uint uMapType);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetKeyboardState(byte[] lpKeyState);

        [DllImport("user32.dll")]
        public static extern int ToUnicode(
            uint wVirtKey,
            uint wScanCode,
            byte[] lpKeyState,
            [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 4)] char[] pwszBuff,
            int cchBuff,
            uint wFlags);
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

        if (e.OldValue is ScreenBuffer oldBuffer)
        {
            if (control._bufferInvalidationHandler is not null)
            {
                oldBuffer.ViewportInvalidated -= control._bufferInvalidationHandler;
            }
            if (control._bufferAppCursorKeysHandler is not null)
            {
                oldBuffer.ApplicationCursorKeysChanged -= control._bufferAppCursorKeysHandler;
            }
        }

        control._bufferInvalidationHandler = control.OnBufferViewportInvalidated;
        control._bufferAppCursorKeysHandler = control.OnBufferApplicationCursorKeysChanged;

        if (e.NewValue is ScreenBuffer newBuffer)
        {
            newBuffer.ViewportInvalidated += control._bufferInvalidationHandler;
            newBuffer.ApplicationCursorKeysChanged += control._bufferAppCursorKeysHandler;
            // Sync initial state so callers that set the buffer after
            // the parser has already toggled DECCKM still get the
            // right keyboard mode without needing a fresh flip.
            control.UseApplicationCursorKeys = newBuffer.ApplicationCursorKeys;
            control._cursorBlinkOn = true;
            control._cursorBlinkTimer.Start();
        }
        else
        {
            control._cursorBlinkTimer.Stop();
        }

        control._renderedRows = 0;
        control._renderedCols = 0;
        control.FullRepaint();
    }

    private static void OnMetricsPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TerminalControl control)
        {
            control._metrics = null;
            control.InvalidateMeasure();
            control._renderedRows = 0;
            control._renderedCols = 0;
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

        dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
        {
            _renderPending = false;
            IncrementalRepaint();
        }));
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

    /// <summary>
    /// Issue #177: bridge <see cref="ScreenBuffer.ApplicationCursorKeys"/>
    /// onto <see cref="UseApplicationCursorKeys"/> so the keyboard
    /// encoder honours DECCKM mode flips driven by the host application
    /// (PSReadLine, vim, ...) without callers having to poll. Reads the
    /// flag back from the sender so a stale fire on a swapped-out
    /// buffer still uses that buffer's current state.
    /// </summary>
    private void OnBufferApplicationCursorKeysChanged(object? sender, EventArgs e)
    {
        if (sender is not ScreenBuffer buffer)
        {
            return;
        }

        var dispatcher = Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            UseApplicationCursorKeys = buffer.ApplicationCursorKeys;
            return;
        }

        dispatcher.BeginInvoke(new Action(() => UseApplicationCursorKeys = buffer.ApplicationCursorKeys));
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
        _renderedCols = buffer.Columns;

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
        if (buffer.Rows != _renderedRows || buffer.Columns != _renderedCols)
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
        _renderedCols = 0;
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
        var brush = new SolidColorBrush(SelectionBrush);
        brush.Freeze();

        if (sel.Mode == SelectionMode.Rectangle)
        {
            var startRow = Math.Clamp(Math.Min(sel.AnchorRow, sel.FocusRow), 1, buffer.Rows);
            var endRow = Math.Clamp(Math.Max(sel.AnchorRow, sel.FocusRow), 1, buffer.Rows);
            var startCol = Math.Clamp(Math.Min(sel.AnchorColumn, sel.FocusColumn), 1, buffer.Columns);
            var endCol = Math.Clamp(Math.Max(sel.AnchorColumn, sel.FocusColumn), 1, buffer.Columns);
            var x = (startCol - 1) * metrics.CellWidth;
            var width = (endCol - startCol + 1) * metrics.CellWidth;
            for (var row = startRow; row <= endRow; row++)
            {
                var y = (row - 1) * metrics.CellHeight;
                dc.DrawRectangle(brush, null, new Rect(x, y, width, metrics.CellHeight));
            }
            return;
        }

        var norm = sel.Normalize();
        var streamStartRow = Math.Clamp(norm.AnchorRow, 1, buffer.Rows);
        var streamEndRow = Math.Clamp(norm.FocusRow, 1, buffer.Rows);
        var streamStartCol = Math.Clamp(norm.AnchorColumn, 1, buffer.Columns);
        var streamEndCol = Math.Clamp(norm.FocusColumn, 1, buffer.Columns);

        for (var row = streamStartRow; row <= streamEndRow; row++)
        {
            var first = row == streamStartRow ? streamStartCol : 1;
            var last = row == streamEndRow ? streamEndCol : buffer.Columns;
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
