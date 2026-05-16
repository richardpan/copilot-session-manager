namespace CopilotSessionManager.Terminal;

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
    /// True when the anchor and focus refer to the same cell — i.e. there
    /// is nothing to copy.
    /// </summary>
    public bool IsEmpty => AnchorRow == FocusRow && AnchorColumn == FocusColumn;

    /// <summary>
    /// Return a selection whose <c>Anchor</c> precedes its <c>Focus</c> in
    /// reading order (earlier row first; same row → smaller column first).
    /// Idempotent.
    /// </summary>
    public TerminalSelection Normalize()
    {
        if (AnchorRow < FocusRow || (AnchorRow == FocusRow && AnchorColumn <= FocusColumn))
        {
            return this;
        }
        return new TerminalSelection(FocusRow, FocusColumn, AnchorRow, AnchorColumn);
    }
}
