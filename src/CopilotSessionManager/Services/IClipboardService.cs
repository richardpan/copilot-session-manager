namespace CopilotSessionManager.Services;

/// <summary>
/// Thin abstraction over the Windows clipboard so view-models can copy
/// text without taking a hard dependency on <c>System.Windows.Clipboard</c>
/// (which is unusable from non-WPF unit tests).
/// </summary>
public interface IClipboardService
{
    /// <summary>
    /// Copies <paramref name="text"/> to the system clipboard. Implementations
    /// should marshal to the UI thread if required and may throw on
    /// transient clipboard failures (the clipboard is a globally-locked
    /// resource).
    /// </summary>
    void SetText(string text);
}
