using System;
using System.Windows;
using CopilotSessionManager.Terminal.Wpf;

namespace CopilotSessionManager.Views.Terminal;

/// <summary>
/// Bridges a <see cref="TerminalControl"/> to a backing terminal session
/// for the embedded tab strip. Owns the two pieces of wiring the strip's
/// XAML can't express on its own:
/// <list type="bullet">
///   <item>Forwarding <see cref="TerminalControl.InputProduced"/> bytes
///   into the session's stdin via the supplied <c>onInput</c> callback.</item>
///   <item>Forwarding pixel-size changes into the session's
///   <see cref="CopilotSessionManager.Terminal.Hosting.TerminalSession.Resize(int, int)"/>
///   via the supplied <c>onResize</c> callback, with cell-count dedup so
///   we never re-issue a no-op resize.</item>
/// </list>
/// <para>
/// Intentionally framework-light: takes plain delegates for the session
/// side so unit tests can construct the binder against a real
/// <see cref="TerminalControl"/> in an STA harness without spinning up
/// a ConPTY. The view code-behind owns the
/// <see cref="System.Windows.Threading.DispatcherTimer"/> debouncer; the
/// binder only worries about "this is the dimension we last applied".
/// </para>
/// </summary>
/// <remarks>
/// Fix for the regression where typing into the embedded terminal did
/// nothing and resizing the dashboard window left the PTY stuck at its
/// initial 30×100 dimensions. The standalone <c>TerminalWindow</c>
/// already had this wiring inline; pulling it into a reusable class
/// keeps the tab-strip code-behind small and gives us a clean seam for
/// tests.
/// </remarks>
public sealed class TerminalControlSessionBinder : IDisposable
{
    private readonly TerminalControl _control;
    private readonly Action<ReadOnlyMemory<byte>> _onInput;
    private readonly Action<int, int> _onResize;
    private readonly EventHandler<TerminalInputEventArgs> _inputHandler;

    private bool _disposed;
    private int _lastRows;
    private int _lastCols;

    /// <summary>
    /// Wire <paramref name="control"/>'s <see cref="TerminalControl.InputProduced"/>
    /// event to <paramref name="onInput"/>. The binder seeds its
    /// last-applied row/column counters with the session's current
    /// dimensions so the first <see cref="TryApplyResize(Size)"/> only
    /// fires when the rendered viewport disagrees with the PTY.
    /// </summary>
    public TerminalControlSessionBinder(
        TerminalControl control,
        Action<ReadOnlyMemory<byte>> onInput,
        Action<int, int> onResize,
        int initialRows,
        int initialCols)
    {
        ArgumentNullException.ThrowIfNull(control);
        ArgumentNullException.ThrowIfNull(onInput);
        ArgumentNullException.ThrowIfNull(onResize);
        if (initialRows <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(initialRows), initialRows, "Must be > 0.");
        }
        if (initialCols <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(initialCols), initialCols, "Must be > 0.");
        }

        _control = control;
        _onInput = onInput;
        _onResize = onResize;
        _lastRows = initialRows;
        _lastCols = initialCols;

        _inputHandler = OnInputProduced;
        _control.InputProduced += _inputHandler;
    }

    /// <summary>Last row count actually forwarded to <c>onResize</c> (defaults to the initial size).</summary>
    public int LastRows => _lastRows;

    /// <summary>Last column count actually forwarded to <c>onResize</c> (defaults to the initial size).</summary>
    public int LastCols => _lastCols;

    /// <summary>True once <see cref="Dispose"/> has been called.</summary>
    public bool IsDisposed => _disposed;

    /// <summary>
    /// Convert <paramref name="pixelSize"/> into cell counts via the
    /// bound control's <see cref="TerminalControl.CellsForViewport(Size)"/>
    /// and forward to the resize callback if (and only if) the cell
    /// dimensions changed from the last applied values. Floors both
    /// dimensions at 2 to keep the PTY happy (ConPTY rejects 0/1).
    /// </summary>
    /// <returns>
    /// <c>true</c> when <c>onResize</c> was invoked, <c>false</c> when
    /// the size was a no-op, the binder was disposed, or the resize
    /// callback threw <see cref="ObjectDisposedException"/> (treated as
    /// "session already torn down — drop the request").
    /// </returns>
    public bool TryApplyResize(Size pixelSize)
    {
        if (_disposed)
        {
            return false;
        }

        var (rows, cols) = _control.CellsForViewport(pixelSize);
        rows = Math.Max(2, rows);
        cols = Math.Max(2, cols);

        if (rows == _lastRows && cols == _lastCols)
        {
            return false;
        }

        try
        {
            _onResize(rows, cols);
        }
        catch (ObjectDisposedException)
        {
            return false;
        }

        _lastRows = rows;
        _lastCols = cols;
        return true;
    }

    private void OnInputProduced(object? sender, TerminalInputEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            _onInput(e.Bytes);
        }
        catch (ObjectDisposedException)
        {
            // Session was torn down between the keystroke and the
            // dispatcher running our handler — drop the bytes silently.
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _control.InputProduced -= _inputHandler;
    }
}
