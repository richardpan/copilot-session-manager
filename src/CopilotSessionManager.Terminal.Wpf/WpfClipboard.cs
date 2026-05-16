using System;
using System.Windows;

namespace CopilotSessionManager.Terminal.Wpf;

/// <summary>
/// Production <see cref="ITerminalClipboard"/> implementation that
/// delegates to <see cref="System.Windows.Clipboard"/>. The WPF clipboard
/// can throw transient COM exceptions if another process is currently
/// holding it; those are swallowed in <see cref="GetText"/> and
/// <see cref="SetText"/> so the terminal stays usable.
/// </summary>
public sealed class WpfClipboard : ITerminalClipboard
{
    /// <inheritdoc />
    public string? GetText()
    {
        try
        {
            return Clipboard.ContainsText() ? Clipboard.GetText() : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public void SetText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }
        try
        {
            Clipboard.SetText(text);
        }
        catch (Exception)
        {
            // Best-effort: clipboard may be locked by another process.
        }
    }
}
