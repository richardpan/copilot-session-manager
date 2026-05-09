namespace CopilotSessionManager.Core.Sessions;

/// <summary>
/// Tunables for <see cref="ISessionStatusEvaluator"/>.
/// </summary>
public sealed class StatusDetectionOptions
{
    /// <summary>
    /// If a session has a live lock but no event newer than this threshold,
    /// it is reported as <see cref="Models.SessionStatus.Idle"/>. Defaults to
    /// 5 minutes.
    /// </summary>
    public TimeSpan IdleThreshold { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Maximum number of events to read from the tail of <c>events.jsonl</c>
    /// when computing status. Larger windows are more accurate when long
    /// permission requests interleave with many other events; the default
    /// (1000) balances accuracy and IO cost.
    /// </summary>
    public int MaxEventsToReplay { get; set; } = 1000;
}
