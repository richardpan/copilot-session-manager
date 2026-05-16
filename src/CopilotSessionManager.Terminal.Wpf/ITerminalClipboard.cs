namespace CopilotSessionManager.Terminal.Wpf;

/// <summary>
/// Tiny abstraction over the system clipboard so the terminal control can
/// be unit-tested without touching real OS clipboard state (which is
/// flaky in CI agents).
/// </summary>
public interface ITerminalClipboard
{
    /// <summary>Return the current clipboard text, or <c>null</c> if none.</summary>
    string? GetText();

    /// <summary>Replace the clipboard with <paramref name="text"/>.</summary>
    void SetText(string text);
}
