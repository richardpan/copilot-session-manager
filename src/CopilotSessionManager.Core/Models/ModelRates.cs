namespace CopilotSessionManager.Core.Models;

/// <summary>
/// Per-million-token USD rates for a single model. All values are
/// <see cref="decimal"/> to keep cost arithmetic exact.
/// </summary>
/// <param name="InputPerMillion">Cost per 1,000,000 fresh input tokens.</param>
/// <param name="OutputPerMillion">Cost per 1,000,000 generated output tokens.</param>
/// <param name="CacheReadPerMillion">Cost per 1,000,000 cached-input tokens read.</param>
/// <param name="CacheWritePerMillion">Cost per 1,000,000 tokens written to cache.</param>
/// <param name="ReasoningPerMillion">
/// Cost per 1,000,000 hidden reasoning tokens. For most models this matches
/// <see cref="OutputPerMillion"/>.
/// </param>
public sealed record ModelRates(
    decimal InputPerMillion,
    decimal OutputPerMillion,
    decimal CacheReadPerMillion,
    decimal CacheWritePerMillion,
    decimal ReasoningPerMillion)
{
    /// <summary>Zeroed rates — used by unknown/free models.</summary>
    public static readonly ModelRates Zero = new(0m, 0m, 0m, 0m, 0m);
}
