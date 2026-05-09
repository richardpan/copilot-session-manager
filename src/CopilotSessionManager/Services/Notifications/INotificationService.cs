using System;

namespace CopilotSessionManager.Services.Notifications;

/// <summary>
/// Severity tag for a transient notification surfaced to the user. Drives
/// the toast icon / sound the underlying surface picks (e.g. a tray balloon
/// vs. a status-bar message).
/// </summary>
public enum NotificationLevel
{
    /// <summary>Informational message ("Merge complete.").</summary>
    Info,

    /// <summary>Recoverable error ("Could not export source session.").</summary>
    Error,
}

/// <summary>
/// Minimal toast/notification surface used by view models that need to tell
/// the user about an out-of-band event (merge complete, sync failed, …)
/// without owning a window.
/// </summary>
/// <remarks>
/// The default WPF implementation routes to the existing system tray icon
/// (<see cref="Tray.ITrayIconService"/>) when present and falls back to a
/// no-op when the tray is unavailable (e.g. in tests). View models should
/// treat this as fire-and-forget — failures must never escape.
/// </remarks>
public interface INotificationService
{
    /// <summary>
    /// Surfaces a transient notification with the given title and body.
    /// Implementations swallow display failures.
    /// </summary>
    void Show(string title, string body, NotificationLevel level = NotificationLevel.Info);
}
