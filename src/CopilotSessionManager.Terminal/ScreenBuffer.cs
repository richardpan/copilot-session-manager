using System;
using System.Collections.Generic;
using System.Text;

namespace CopilotSessionManager.Terminal;

/// <summary>
/// Two-dimensional cell grid that consumes <see cref="VtEvent"/> values
/// and applies them to a model of the terminal. Phase 2B of epic #93.
/// </summary>
/// <remarks>
/// <para>
/// The buffer maintains a primary screen, an alternate screen (toggled
/// via DEC private mode 1049), a current pen (<see cref="Style"/>), the
/// cursor, a scroll-back ring (primary only), per-row dirty tracking for
/// the renderer, and a small amount of host-state (window title,
/// bracketed-paste flag).
/// </para>
/// <para>
/// All rows and columns in the public API are 1-based to match the
/// VT/CSI conventions consumers will already be reasoning in.
/// </para>
/// <para>
/// Thread-safety: not safe for concurrent calls. The intended caller is
/// the same single dedicated reader task that drives the parser.
/// </para>
/// </remarks>
public sealed class ScreenBuffer
{
    private const int DefaultScrollbackCapacity = 1000;

    private TerminalCell[] _primary;
    private TerminalCell[] _alternate;
    private TerminalCell[] _active;
    private bool[] _dirtyRows;

    private int _cursorRow0;
    private int _cursorCol0;
    private bool _cursorVisible = true;
    private bool _pendingWrap;

    private bool _usingAlternate;

    // Save / restore state. xterm keeps separate save state per buffer;
    // we use one slot per buffer for parity.
    private SavedCursor? _primarySaved;
    private SavedCursor? _alternateSaved;

    private readonly LinkedList<TerminalCell[]> _scrollback = new();
    private readonly int _scrollbackCapacity;

    /// <summary>Pen used to stamp newly printed cells.</summary>
    public TerminalStyle Style { get; } = new();

    /// <summary>Number of rows.</summary>
    public int Rows { get; private set; }

    /// <summary>Number of columns.</summary>
    public int Columns { get; private set; }

    /// <summary>1-based cursor row.</summary>
    public int CursorRow => _cursorRow0 + 1;

    /// <summary>1-based cursor column.</summary>
    public int CursorColumn => _cursorCol0 + 1;

    /// <summary>True if the cursor should be drawn (DECTCEM).</summary>
    public bool CursorVisible => _cursorVisible;

    /// <summary>True while the alternate screen buffer is active (DEC 1049).</summary>
    public bool UsingAlternateScreen => _usingAlternate;

    /// <summary>Most recent OSC 0/1/2 window title.</summary>
    public string WindowTitle { get; private set; } = string.Empty;

    /// <summary>Whether bracketed-paste mode is enabled (DEC 2004).</summary>
    public bool BracketedPasteEnabled { get; private set; }

    /// <summary>
    /// Whether DECCKM (DEC private mode 1) is enabled. When <c>true</c>
    /// the host should encode cursor / Home / End keys as the
    /// application-mode SS3 sequences. Defaults to <c>false</c>;
    /// PSReadLine, vim and similar tools flip this on init via
    /// <c>ESC [ ? 1 h</c>. Subscribe to
    /// <see cref="ApplicationCursorKeysChanged"/> to react.
    /// </summary>
    public bool ApplicationCursorKeys { get; private set; }

    /// <summary>
    /// Raised after <see cref="ApplicationCursorKeys"/> changes value.
    /// Fires on the buffer's mutation thread; consumers that touch UI
    /// state should marshal to the UI dispatcher themselves.
    /// </summary>
    public event EventHandler? ApplicationCursorKeysChanged;

    /// <summary>Number of lines currently held in the scroll-back ring.</summary>
    public int ScrollbackLineCount => _scrollback.Count;

    /// <summary>Maximum number of lines kept in the scroll-back ring.</summary>
    public int ScrollbackCapacity => _scrollbackCapacity;

    /// <summary>
    /// Per-row "dirty" flags — true when the row's contents have changed
    /// since the last <see cref="ClearDirty"/>. Same length as
    /// <see cref="Rows"/>.
    /// </summary>
    public IReadOnlyList<bool> DirtyRows => _dirtyRows;

    /// <summary>True if any row is currently dirty.</summary>
    public bool HasDirtyRows
    {
        get
        {
            for (var i = 0; i < _dirtyRows.Length; i++)
            {
                if (_dirtyRows[i])
                    return true;
            }
            return false;
        }
    }

    /// <summary>Create a buffer of the given size with a default scroll-back capacity.</summary>
    public ScreenBuffer(int rows, int columns)
        : this(rows, columns, DefaultScrollbackCapacity) { }

    /// <summary>Create a buffer with explicit scroll-back capacity.</summary>
    public ScreenBuffer(int rows, int columns, int scrollbackCapacity)
    {
        if (rows <= 0)
            throw new ArgumentOutOfRangeException(nameof(rows));
        if (columns <= 0)
            throw new ArgumentOutOfRangeException(nameof(columns));
        if (scrollbackCapacity < 0)
            throw new ArgumentOutOfRangeException(nameof(scrollbackCapacity));

        Rows = rows;
        Columns = columns;
        _scrollbackCapacity = scrollbackCapacity;
        _primary = CreateBlankBuffer(rows, columns);
        _alternate = CreateBlankBuffer(rows, columns);
        _active = _primary;
        _dirtyRows = new bool[rows];
    }

    // -- public surface --------------------------------------------------

    /// <summary>Get a cell from the active buffer (1-based indices).</summary>
    public TerminalCell GetCell(int row, int column)
    {
        EnsureInBounds(row, column);
        return _active[Index(row - 1, column - 1)];
    }

    /// <summary>
    /// Get a cell from the scroll-back ring. <paramref name="line"/> is
    /// 0-based with 0 = oldest line (the line that scrolled off first).
    /// </summary>
    public TerminalCell GetScrollbackCell(int line, int column)
    {
        if (line < 0 || line >= _scrollback.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(line));
        }
        if (column < 1 || column > Columns)
        {
            throw new ArgumentOutOfRangeException(nameof(column));
        }
        var node = _scrollback.First!;
        for (var i = 0; i < line; i++)
            node = node.Next!;
        return node.Value[column - 1];
    }

    /// <summary>
    /// Copy scrollback row arrays into <paramref name="dest"/> starting
    /// from line <paramref name="startLine"/> (0-based, oldest-first).
    /// Walks the linked list once for efficiency. Returns the number of
    /// rows actually copied.
    /// </summary>
    internal int GetScrollbackRows(int startLine, int count, TerminalCell[][] dest)
    {
        if (startLine < 0 || startLine >= _scrollback.Count || count <= 0)
            return 0;

        var node = _scrollback.First!;
        for (var i = 0; i < startLine; i++)
            node = node.Next!;

        var copied = 0;
        for (var i = 0; i < count && node != null; i++, node = node.Next!)
        {
            dest[i] = node.Value;
            copied++;
        }
        return copied;
    }

    /// <summary>Reset the dirty-row tracking after the renderer has caught up.</summary>
    public void ClearDirty()
    {
        Array.Clear(_dirtyRows);
    }

    /// <summary>
    /// Raised after a mutation (<see cref="Apply(VtEvent)"/>,
    /// <see cref="Resize(int, int)"/>, <see cref="Reset"/>) has touched the
    /// buffer. Subscribers should consult <see cref="DirtyRows"/> /
    /// <see cref="HasDirtyRows"/> to determine what to repaint, then call
    /// <see cref="ClearDirty"/>. Phase 3B of epic #93.
    /// </summary>
    /// <remarks>
    /// The event fires synchronously on the thread that performed the
    /// mutation. Subscribers that touch UI state must marshal to their
    /// dispatcher; the buffer itself remains thread-affine to its writer.
    /// Cursor moves, alternate-screen swaps, title changes, and any other
    /// state visible to a renderer also raise this event even when no row
    /// is marked dirty — renderers may need to repaint the cursor visual
    /// or react to a title change without any cell content shifting.
    /// </remarks>
    public event EventHandler? ViewportInvalidated;

    /// <summary>Apply a sequence of events (convenience for <see cref="Apply(VtEvent)"/>).</summary>
    public void ApplyAll(IEnumerable<VtEvent> events)
    {
        if (events is null)
            throw new ArgumentNullException(nameof(events));
        foreach (var e in events)
            Apply(e);
    }

    /// <summary>Apply a single parsed event to the buffer.</summary>
    public void Apply(VtEvent evt)
    {
        switch (evt)
        {
            case PrintRune(var glyph):
                HandlePrintRune(glyph);
                break;
            case LineFeed:
                HandleLineFeed();
                break;
            case CarriageReturn:
                HandleCarriageReturn();
                break;
            case Backspace:
                HandleBackspace();
                break;
            case HorizontalTab:
                HandleTab();
                break;
            case RingBell:
                break; // model has nothing to do; renderer may flash

            case SetCursorPosition(var r, var c):
                HandleCursorPosition(r, c);
                break;
            case MoveCursorUp(var n):
                HandleMoveUp(n);
                break;
            case MoveCursorDown(var n):
                HandleMoveDown(n);
                break;
            case MoveCursorForward(var n):
                HandleMoveForward(n);
                break;
            case MoveCursorBack(var n):
                HandleMoveBack(n);
                break;
            case SaveCursor:
                HandleSaveCursor();
                break;
            case RestoreCursor:
                HandleRestoreCursor();
                break;

            case EraseInDisplay(var mode):
                HandleEraseInDisplay(mode);
                break;
            case EraseInLine(var mode):
                HandleEraseInLine(mode);
                break;
            case ScrollUp(var n):
                HandleScrollUp(n);
                break;
            case ScrollDown(var n):
                HandleScrollDown(n);
                break;

            case SetGraphicsRendition(var parameters):
                foreach (var p in parameters)
                    Style.Apply(p);
                break;

            case SetCursorVisibility(var v):
                _cursorVisible = v;
                break;
            case SetUseAlternateScreen(var use):
                HandleAlternateScreen(use);
                break;
            case SetBracketedPaste(var enabled):
                BracketedPasteEnabled = enabled;
                break;
            case SetApplicationCursorKeys(var ackEnabled):
                if (ApplicationCursorKeys != ackEnabled)
                {
                    ApplicationCursorKeys = ackEnabled;
                    ApplicationCursorKeysChanged?.Invoke(this, EventArgs.Empty);
                }
                break;
            case SetMode:
                break;

            case SetWindowTitle(var title):
                WindowTitle = title;
                break;
            case ResetTerminal:
                Reset();
                break;
            case UnknownSequence:
                break;
        }

        ViewportInvalidated?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Resize to a new geometry. Existing content in the top-left corner
    /// is preserved; new cells are blank and the cursor is clamped into
    /// the new bounds. Reflowing wrapped lines is intentionally out of
    /// scope for Phase 2B.
    /// </summary>
    public void Resize(int rows, int columns)
    {
        if (rows <= 0)
            throw new ArgumentOutOfRangeException(nameof(rows));
        if (columns <= 0)
            throw new ArgumentOutOfRangeException(nameof(columns));
        if (rows == Rows && columns == Columns)
            return;

        _primary = ResizeBuffer(_primary, Rows, Columns, rows, columns);
        _alternate = ResizeBuffer(_alternate, Rows, Columns, rows, columns);
        _active = _usingAlternate ? _alternate : _primary;
        _dirtyRows = new bool[rows];
        for (var i = 0; i < rows; i++)
            _dirtyRows[i] = true;

        Rows = rows;
        Columns = columns;

        _cursorRow0 = Math.Min(_cursorRow0, rows - 1);
        _cursorCol0 = Math.Min(_cursorCol0, columns - 1);
        _pendingWrap = false;

        ViewportInvalidated?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Full terminal reset (RIS / ESC c).</summary>
    public void Reset()
    {
        Style.Reset();
        Array.Fill(_primary, TerminalCell.Empty);
        Array.Fill(_alternate, TerminalCell.Empty);
        _active = _primary;
        _usingAlternate = false;
        _cursorRow0 = 0;
        _cursorCol0 = 0;
        _cursorVisible = true;
        _pendingWrap = false;
        _primarySaved = null;
        _alternateSaved = null;
        BracketedPasteEnabled = false;
        if (ApplicationCursorKeys)
        {
            ApplicationCursorKeys = false;
            ApplicationCursorKeysChanged?.Invoke(this, EventArgs.Empty);
        }
        WindowTitle = string.Empty;
        _scrollback.Clear();
        for (var i = 0; i < _dirtyRows.Length; i++)
            _dirtyRows[i] = true;

        ViewportInvalidated?.Invoke(this, EventArgs.Empty);
    }

    // -- handlers --------------------------------------------------------

    private void HandlePrintRune(Rune glyph)
    {
        if (_pendingWrap)
        {
            _cursorCol0 = 0;
            AdvanceRowOrScroll();
            _pendingWrap = false;
        }

        _active[Index(_cursorRow0, _cursorCol0)] = Style.Stamp(glyph);
        MarkRowDirty(_cursorRow0);

        if (_cursorCol0 + 1 >= Columns)
        {
            _pendingWrap = true;
        }
        else
        {
            _cursorCol0++;
        }
    }

    private void HandleLineFeed()
    {
        AdvanceRowOrScroll();
        _pendingWrap = false;
    }

    private void HandleCarriageReturn()
    {
        _cursorCol0 = 0;
        _pendingWrap = false;
    }

    private void HandleBackspace()
    {
        if (_cursorCol0 > 0)
            _cursorCol0--;
        _pendingWrap = false;
    }

    private void HandleTab()
    {
        var next = ((_cursorCol0 / 8) + 1) * 8;
        _cursorCol0 = Math.Min(next, Columns - 1);
        _pendingWrap = false;
    }

    private void HandleCursorPosition(int row1, int col1)
    {
        _cursorRow0 = Clamp(row1 - 1, 0, Rows - 1);
        _cursorCol0 = Clamp(col1 - 1, 0, Columns - 1);
        _pendingWrap = false;
    }

    private void HandleMoveUp(int n)
    {
        _cursorRow0 = Math.Max(0, _cursorRow0 - Math.Max(1, n));
        _pendingWrap = false;
    }

    private void HandleMoveDown(int n)
    {
        _cursorRow0 = Math.Min(Rows - 1, _cursorRow0 + Math.Max(1, n));
        _pendingWrap = false;
    }

    private void HandleMoveForward(int n)
    {
        _cursorCol0 = Math.Min(Columns - 1, _cursorCol0 + Math.Max(1, n));
        _pendingWrap = false;
    }

    private void HandleMoveBack(int n)
    {
        _cursorCol0 = Math.Max(0, _cursorCol0 - Math.Max(1, n));
        _pendingWrap = false;
    }

    private void HandleSaveCursor()
    {
        var snap = new SavedCursor(_cursorRow0, _cursorCol0, Style.Snapshot(), _cursorVisible);
        if (_usingAlternate)
            _alternateSaved = snap;
        else
            _primarySaved = snap;
    }

    private void HandleRestoreCursor()
    {
        var snap = _usingAlternate ? _alternateSaved : _primarySaved;
        if (snap is null)
        {
            _cursorRow0 = 0;
            _cursorCol0 = 0;
            Style.Reset();
            _cursorVisible = true;
        }
        else
        {
            _cursorRow0 = snap.Value.Row;
            _cursorCol0 = snap.Value.Column;
            Style.Restore(snap.Value.Style);
            _cursorVisible = snap.Value.Visible;
        }
        _pendingWrap = false;
    }

    private void HandleEraseInDisplay(EraseMode mode)
    {
        var blank = Style.Stamp(new Rune(' '));
        switch (mode)
        {
            case EraseMode.ToEnd:
                FillRow(_cursorRow0, _cursorCol0, Columns - 1, blank);
                for (var r = _cursorRow0 + 1; r < Rows; r++)
                {
                    FillRow(r, 0, Columns - 1, blank);
                }
                break;
            case EraseMode.ToStart:
                for (var r = 0; r < _cursorRow0; r++)
                {
                    FillRow(r, 0, Columns - 1, blank);
                }
                FillRow(_cursorRow0, 0, _cursorCol0, blank);
                break;
            case EraseMode.All:
                for (var r = 0; r < Rows; r++)
                {
                    FillRow(r, 0, Columns - 1, blank);
                }
                break;
            case EraseMode.Scrollback:
                _scrollback.Clear();
                break;
        }
    }

    private void HandleEraseInLine(EraseMode mode)
    {
        var blank = Style.Stamp(new Rune(' '));
        switch (mode)
        {
            case EraseMode.ToEnd:
                FillRow(_cursorRow0, _cursorCol0, Columns - 1, blank);
                break;
            case EraseMode.ToStart:
                FillRow(_cursorRow0, 0, _cursorCol0, blank);
                break;
            case EraseMode.All:
                FillRow(_cursorRow0, 0, Columns - 1, blank);
                break;
            case EraseMode.Scrollback:
                // Not meaningful for EL; ignore.
                break;
        }
    }

    private void HandleScrollUp(int n)
    {
        n = Math.Max(1, n);
        for (var i = 0; i < n; i++)
            ScrollUpOnce();
    }

    private void HandleScrollDown(int n)
    {
        n = Math.Max(1, n);
        for (var i = 0; i < n; i++)
            ScrollDownOnce();
    }

    private void HandleAlternateScreen(bool use)
    {
        if (use == _usingAlternate)
            return;

        _usingAlternate = use;
        _active = use ? _alternate : _primary;

        if (use)
        {
            // xterm's 1049 sequence clears the alternate buffer on entry.
            Array.Fill(_alternate, TerminalCell.Empty);
        }

        for (var i = 0; i < _dirtyRows.Length; i++)
            _dirtyRows[i] = true;
        _pendingWrap = false;
    }

    // -- scroll & fill helpers ------------------------------------------

    private void AdvanceRowOrScroll()
    {
        if (_cursorRow0 + 1 >= Rows)
        {
            ScrollUpOnce();
        }
        else
        {
            _cursorRow0++;
        }
    }

    private void ScrollUpOnce()
    {
        // Detach the top row first so we can hand it to scroll-back.
        var topRow = ExtractRow(0);

        if (!_usingAlternate)
        {
            PushScrollback(topRow);
        }

        // Shift rows [1..Rows-1] up by one slot.
        Array.Copy(_active, Columns, _active, 0, (Rows - 1) * Columns);
        FillRow(Rows - 1, 0, Columns - 1, Style.Stamp(new Rune(' ')));

        for (var r = 0; r < Rows; r++)
            _dirtyRows[r] = true;
    }

    private void ScrollDownOnce()
    {
        // Shift rows [0..Rows-2] down by one slot; bottom row is dropped.
        Array.Copy(_active, 0, _active, Columns, (Rows - 1) * Columns);
        FillRow(0, 0, Columns - 1, Style.Stamp(new Rune(' ')));

        for (var r = 0; r < Rows; r++)
            _dirtyRows[r] = true;
    }

    private TerminalCell[] ExtractRow(int row0)
    {
        var copy = new TerminalCell[Columns];
        Array.Copy(_active, row0 * Columns, copy, 0, Columns);
        return copy;
    }

    private void PushScrollback(TerminalCell[] row)
    {
        if (_scrollbackCapacity == 0)
            return;
        _scrollback.AddLast(row);
        while (_scrollback.Count > _scrollbackCapacity)
        {
            _scrollback.RemoveFirst();
        }
    }

    private void FillRow(int row0, int startCol, int endColInclusive, TerminalCell cell)
    {
        var baseIdx = row0 * Columns;
        for (var c = startCol; c <= endColInclusive; c++)
        {
            _active[baseIdx + c] = cell;
        }
        MarkRowDirty(row0);
    }

    private void MarkRowDirty(int row0) => _dirtyRows[row0] = true;

    private int Index(int row0, int col0) => row0 * Columns + col0;

    private static int Clamp(int v, int lo, int hi) => v < lo ? lo : v > hi ? hi : v;

    private void EnsureInBounds(int row, int column)
    {
        if (row < 1 || row > Rows)
            throw new ArgumentOutOfRangeException(nameof(row));
        if (column < 1 || column > Columns)
            throw new ArgumentOutOfRangeException(nameof(column));
    }

    private static TerminalCell[] CreateBlankBuffer(int rows, int columns)
    {
        var buffer = new TerminalCell[rows * columns];
        Array.Fill(buffer, TerminalCell.Empty);
        return buffer;
    }

    private static TerminalCell[] ResizeBuffer(
        TerminalCell[] source, int oldRows, int oldCols, int newRows, int newCols)
    {
        var next = CreateBlankBuffer(newRows, newCols);
        var copyRows = Math.Min(oldRows, newRows);
        var copyCols = Math.Min(oldCols, newCols);
        for (var r = 0; r < copyRows; r++)
        {
            Array.Copy(source, r * oldCols, next, r * newCols, copyCols);
        }
        return next;
    }

    private readonly record struct SavedCursor(int Row, int Column, TerminalStyleSnapshot Style, bool Visible);
}
