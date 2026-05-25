using System;
using System.Collections.Generic;

namespace CopilotSessionManager.Terminal;

/// <summary>
/// Periodically captures the terminal screen content and pushes unique
/// rows into the <see cref="ScreenBuffer"/> scrollback.
/// <para>
/// This solves the problem where full-screen TUI apps (Copilot CLI, vim,
/// etc.) use cursor repositioning instead of LF-based scrolling, which
/// means the normal scrollback mechanism captures almost nothing.
/// </para>
/// <para>
/// Call <see cref="TryCapture"/> from the UI thread on a timer or render
/// callback. It is self-throttling (minimum interval between captures)
/// and deduplicates content to avoid repeated rows in scrollback.
/// </para>
/// </summary>
public sealed class ScreenTranscript
{
    private const long CaptureIntervalMs = 3000; // 3 seconds
    private const int DedupeWindowSize = 500;     // last N row hashes to remember

    private readonly ScreenBuffer _buffer;

    // Previous screen snapshot — text per row + raw cells for fidelity.
    private string[]? _prevText;
    private TerminalCell[][]? _prevCells;
    private int _prevRows;
    private int _prevCols;

    // Timing.
    private long _lastCaptureTickMs;

    // Content dedup — hashes of recently pushed rows.
    private readonly HashSet<int> _recentHashes = new();
    private readonly Queue<int> _recentQueue = new();

    public ScreenTranscript(ScreenBuffer buffer)
    {
        _buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
    }

    /// <summary>
    /// Attempt to capture the current screen content. Self-throttles to
    /// at most once per <see cref="CaptureIntervalMs"/>. Only captures
    /// when in alternate screen mode (normal LF-based scrollback handles
    /// the primary buffer).
    /// </summary>
    public void TryCapture()
    {
        // Only capture in alternate screen mode — the explicit LF-based
        // scrollback path handles the primary buffer perfectly.
        if (!_buffer.UsingAlternateScreen)
            return;

        var now = Environment.TickCount64;
        if (now - _lastCaptureTickMs < CaptureIntervalMs)
            return;

        _lastCaptureTickMs = now;

        var rows = _buffer.Rows;
        var cols = _buffer.Columns;

        // Take a text + cell snapshot of the current screen.
        var currentText = new string[rows];
        var currentCells = new TerminalCell[rows][];
        for (var r = 0; r < rows; r++)
        {
            currentText[r] = _buffer.GetRowText(r);
            currentCells[r] = _buffer.GetRowCells(r);
        }

        // First capture or geometry changed — just snapshot and return.
        if (_prevText is null || _prevRows != rows || _prevCols != cols)
        {
            _prevText = currentText;
            _prevCells = currentCells;
            _prevRows = rows;
            _prevCols = cols;
            return;
        }

        // Compare each old row against the new screen.
        // Only push rows that:
        //   1) Changed between snapshots
        //   2) Have meaningful content (not blank)
        //   3) Are NOT a prefix of the same-position new row (streaming)
        //   4) Don't appear anywhere on the new screen (truly gone)
        //   5) Haven't been pushed recently (dedup)
        for (var r = 0; r < Math.Min(rows, _prevRows); r++)
        {
            var oldText = _prevText[r];
            var newText = currentText[r];

            // Skip unchanged rows.
            if (oldText == newText)
                continue;

            // Skip blank rows.
            if (string.IsNullOrWhiteSpace(oldText))
                continue;

            var oldTrimmed = oldText.TrimEnd();

            // Skip streaming extensions — the new text starts with the old
            // text (copilot added more tokens to the same line).
            var newTrimmed = newText.TrimEnd();
            if (newTrimmed.Length > oldTrimmed.Length &&
                newTrimmed.StartsWith(oldTrimmed, StringComparison.Ordinal))
                continue;

            // Skip if the old text still appears anywhere on the new screen
            // (content shifted to a different row, not gone).
            if (IsTextOnScreen(oldTrimmed, currentText))
                continue;

            // Content-hash dedup.
            var hash = oldTrimmed.GetHashCode(StringComparison.Ordinal);
            if (_recentHashes.Contains(hash))
                continue;

            // This row genuinely scrolled off — push OLD cells to scrollback.
            _buffer.PushExternalScrollback(_prevCells![r]);
            TrackHash(hash);
        }

        _prevText = currentText;
        _prevCells = currentCells;
    }

    private static bool IsTextOnScreen(string text, string[] snapshot)
    {
        for (var r = 0; r < snapshot.Length; r++)
        {
            var rowTrimmed = snapshot[r].TrimEnd();
            if (rowTrimmed == text)
                return true;
            // Also check if the screen row extends the text (streaming).
            if (rowTrimmed.Length > text.Length &&
                rowTrimmed.StartsWith(text, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private void TrackHash(int hash)
    {
        _recentHashes.Add(hash);
        _recentQueue.Enqueue(hash);
        while (_recentQueue.Count > DedupeWindowSize)
            _recentHashes.Remove(_recentQueue.Dequeue());
    }

    /// <summary>
    /// Reset all state (e.g. on terminal resize or reset).
    /// </summary>
    public void Reset()
    {
        _prevText = null;
        _prevCells = null;
        _recentHashes.Clear();
        _recentQueue.Clear();
        _lastCaptureTickMs = 0;
    }
}
