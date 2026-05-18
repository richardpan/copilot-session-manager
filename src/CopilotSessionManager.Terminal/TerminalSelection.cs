namespace CopilotSessionManager.Terminal;

/// <summary>
/// How the cells covered by a <see cref="TerminalSelection"/> should be
/// interpreted when extracting text or rendering the selection visual.
/// </summary>
public enum SelectionMode
{
    /// <summary>
    /// Reading-order "stream" selection: from the anchor cell to the end of
    /// its row, every intermediate row in full, and from the start of the
    /// focus row to the focus cell. This is the default left-drag mouse
    /// behaviour.
    /// </summary>
    Stream = 0,

    /// <summary>
    /// Rectangular ("block" or "column") selection: each row in the span is
    /// sliced to the same column range, [min(AnchorColumn, FocusColumn),
    /// max(AnchorColumn, FocusColumn)]. Triggered by Alt+drag (issue #179).
    /// </summary>
    Rectangle = 1,
}

/// <summary>
/// A reading-order text selection spanning the terminal grid. Both endpoints
/// are 1-based and inclusive. <c>Anchor</c> is the cell where the user
/// started selecting; <c>Focus</c> is where the selection currently ends
/// (typically tracks the mouse). Use <see cref="Normalize"/> to swap the
/// endpoints so the result reads top-to-bottom, left-to-right.
/// </summary>
public sealed record TerminalSelection(int AnchorRow, int AnchorColumn, int FocusRow, int FocusColumn)
{
    /// <summary>
    /// Interpretation of the cells covered by this selection. Defaults to
    /// <see cref="SelectionMode.Stream"/>; <see cref="SelectionMode.Rectangle"/>
    /// is set via <c>with { Mode = ... }</c> for Alt+drag block selection.
    /// </summary>
    public SelectionMode Mode { get; init; } = SelectionMode.Stream;

    /// <summary>
    /// True when the anchor and focus refer to the same cell — i.e. there
    /// is nothing to copy.
    /// </summary>
    public bool IsEmpty => AnchorRow == FocusRow && AnchorColumn == FocusColumn;

    /// <summary>
    /// Return a selection whose <c>Anchor</c> precedes its <c>Focus</c> in
    /// reading order (earlier row first; same row → smaller column first).
    /// Idempotent. Preserves <see cref="Mode"/>.
    /// </summary>
    public TerminalSelection Normalize()
    {
        if (AnchorRow < FocusRow || (AnchorRow == FocusRow && AnchorColumn <= FocusColumn))
        {
            return this;
        }
        return new TerminalSelection(FocusRow, FocusColumn, AnchorRow, AnchorColumn) { Mode = Mode };
    }
}
