using System;
using CopilotSessionManager.Services.Tray;
using Microsoft.Extensions.Logging;

namespace CopilotSessionManager.Services.Notifications;

/// <summary>
/// Default <see cref="INotificationService"/>: routes to the system tray
/// icon's balloon notification surface. Failures are swallowed and logged
/// — callers may fire-and-forget.
/// </summary>
public sealed class TrayNotificationService : INotificationService
{
    private readonly ITrayIconService _tray;
    private readonly ILogger<TrayNotificationService> _logger;

    public TrayNotificationService(ITrayIconService tray, ILogger<TrayNotificationService> logger)
    {
        ArgumentNullException.ThrowIfNull(tray);
        ArgumentNullException.ThrowIfNull(logger);
        _tray = tray;
        _logger = logger;
    }

    public void Show(string title, string body, NotificationLevel level = NotificationLevel.Info)
    {
        if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(body))
        {
            return;
        }

        try
        {
            _tray.ShowNotification(title, body, isError: level == NotificationLevel.Error);
            _logger.LogInformation(
                "Notification ({Level}) — {Title}", level, title);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to surface notification: {Title}", title);
        }
    }
}
