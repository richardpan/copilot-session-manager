namespace CopilotSessionManager.Services.Notifications;

/// <summary>
/// No-op <see cref="INotificationService"/> for tests + headless contexts
/// where surfacing a real toast would be inappropriate. Records the last
/// notification to make assertions easy.
/// </summary>
public sealed class NoopNotificationService : INotificationService
{
    /// <summary>The most recent (title, body, level) tuple, or <c>null</c>.</summary>
    public (string Title, string Body, NotificationLevel Level)? LastNotification { get; private set; }

    /// <summary>How many times <see cref="Show"/> has been called.</summary>
    public int CallCount { get; private set; }

    public void Show(string title, string body, NotificationLevel level = NotificationLevel.Info)
    {
        CallCount++;
        LastNotification = (title ?? string.Empty, body ?? string.Empty, level);
    }
}
