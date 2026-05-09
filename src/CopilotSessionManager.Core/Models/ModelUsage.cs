namespace CopilotSessionManager.Core.Models;

/// <summary>
/// Token usage attributed to a single model within a session. Mirrors the
/// shape Copilot CLI emits in <c>session.shutdown.modelMetrics[id].usage</c>.
/// </summary>
/// <param name="InputTokens">Fresh input tokens (uncached).</param>
/// <param name="OutputTokens">Generated output tokens.</param>
/// <param name="CacheReadTokens">Tokens served from the prompt cache.</param>
/// <param name="CacheWriteTokens">Tokens written to the prompt cache.</param>
/// <param name="ReasoningTokens">Hidden reasoning tokens (where applicable).</param>
/// <param name="RequestCount">Number of model invocations.</param>
public sealed record ModelUsage(
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheWriteTokens,
    long ReasoningTokens,
    int RequestCount)
{
    /// <summary>Empty usage — no requests, no tokens.</summary>
    public static readonly ModelUsage Zero = new(0, 0, 0, 0, 0, 0);

    /// <summary>Total tokens across all categories. Convenience accessor.</summary>
    public long TotalTokens =>
        InputTokens + OutputTokens + CacheReadTokens + CacheWriteTokens + ReasoningTokens;
}
