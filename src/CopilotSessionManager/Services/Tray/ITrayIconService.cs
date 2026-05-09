using System;

namespace CopilotSessionManager.Services.Tray;

/// <summary>
/// Owns the system tray (notification area) icon for the application. The
/// concrete implementation hosts a Windows Forms <c>NotifyIcon</c>; this
/// interface keeps the WPF host (and tests) decoupled from that detail so
/// startup wiring can be exercised without spawning a real tray icon.
/// </summary>
/// <remarks>
/// Lifecycle: construct → call <see cref="Show"/> once after the host is
/// up → wire the events → call <see cref="Hide"/> if the user explicitly
/// quits via the tray menu (so the icon disappears immediately rather than
/// "ghosting" until the next mouse-over). Disposal removes the icon.
/// </remarks>
public interface ITrayIconService : IDisposable
{
    /// <summary>Raised when the user left-clicks the tray icon (request to restore the main window).</summary>
    event EventHandler? ActivateRequested;

    /// <summary>Raised when the user picks "Open" from the tray context menu.</summary>
    event EventHandler? OpenRequested;

    /// <summary>Raised when the user picks "Quit" from the tray context menu.</summary>
    event EventHandler? QuitRequested;

    /// <summary>Makes the tray icon visible. Idempotent.</summary>
    void Show();

    /// <summary>Hides the tray icon (does not dispose). Idempotent.</summary>
    void Hide();

    /// <summary>
    /// Updates the tray icon's tooltip with the current count of sessions
    /// awaiting input. Pass 0 to render the plain product name without a
    /// suffix. The icon image itself is unchanged in V1 — proper icon
    /// overlays land in a follow-up.
    /// </summary>
    void UpdateAwaitingInputCount(int count);

    /// <summary>
    /// Surfaces a transient balloon notification next to the tray icon.
    /// <paramref name="isError"/> picks the warning/info glyph. Best-effort:
    /// implementations swallow display failures so callers can fire-and-forget.
    /// </summary>
    void ShowNotification(string title, string body, bool isError = false);
}
