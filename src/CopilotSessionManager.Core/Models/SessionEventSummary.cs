namespace CopilotSessionManager.Core.Models;

/// <summary>
/// Aggregated picture of a session's <c>events.jsonl</c> stream, used by the
/// README renderer to fill in the auto-generated activity sections (Recent
/// prompts / Tool usage / Activity gaps). Pure data — no IO.
/// </summary>
/// <param name="RecentPrompts">
/// The most recent <c>user.message</c> entries (newest first), capped at
/// <see cref="SessionEventSummary.MaxRecentPrompts"/> and individually
/// truncated to <see cref="SessionEventSummary.MaxPromptBodyChars"/>
/// characters with an ellipsis suffix.
/// </param>
/// <param name="TopTools">
/// Top <see cref="SessionEventSummary.MaxTopTools"/> tools by
/// <c>tool.execution_start</c> count, descending.
/// </param>
/// <param name="LongestIdleGap">
/// The largest pause between consecutive events. <c>null</c> when there are
/// fewer than two events.
/// </param>
/// <param name="TotalActiveSpan">
/// Time between the first and last event in the stream. <c>null</c> when
/// there are no events.
/// </param>
/// <param name="TotalEvents">
/// Total number of events successfully parsed.
/// </param>
public sealed record SessionEventSummary(
    IReadOnlyList<RecentPrompt> RecentPrompts,
    IReadOnlyList<ToolUsageCount> TopTools,
    TimeSpan? LongestIdleGap,
    TimeSpan? TotalActiveSpan,
    int TotalEvents)
{
    /// <summary>How many recent user.message entries to keep.</summary>
    public const int MaxRecentPrompts = 5;

    /// <summary>Per-prompt truncation length in characters.</summary>
    public const int MaxPromptBodyChars = 240;

    /// <summary>How many tools to surface in the histogram.</summary>
    public const int MaxTopTools = 10;

    /// <summary>Empty summary used when a session has no events.jsonl.</summary>
    public static SessionEventSummary Empty { get; } = new(
        Array.Empty<RecentPrompt>(),
        Array.Empty<ToolUsageCount>(),
        null,
        null,
        0);
}

/// <summary>
/// A single user-typed message extracted from a <c>user.message</c> event.
/// </summary>
/// <param name="Timestamp">UTC time the message was logged.</param>
/// <param name="Body">
/// User-visible body, already truncated to
/// <see cref="SessionEventSummary.MaxPromptBodyChars"/>. Newlines are
/// collapsed to spaces so the value renders cleanly in a markdown bullet.
/// </param>
public sealed record RecentPrompt(
    DateTimeOffset Timestamp,
    string Body);

/// <summary>
/// A tool name plus the number of <c>tool.execution_start</c> events recorded
/// for it.
/// </summary>
public sealed record ToolUsageCount(
    string ToolName,
    int Count);
