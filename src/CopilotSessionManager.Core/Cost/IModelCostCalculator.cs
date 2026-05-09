using CopilotSessionManager.Core.Models;

namespace CopilotSessionManager.Core.Cost;

/// <summary>
/// Computes an estimated USD cost for a session given the model usage
/// recorded in <see cref="SessionModelInfo"/>.
/// </summary>
public interface IModelCostCalculator
{
    /// <summary>
    /// Returns the estimated USD cost for <paramref name="info"/>, or
    /// <see langword="null"/> when no token counts are available (typical for
    /// active sessions that have not yet emitted a <c>session.shutdown</c>
    /// event).
    /// </summary>
    /// <remarks>
    /// Models not present in <see cref="Cli.IModelCatalog"/> contribute
    /// nothing. <see cref="EstimateResult.HasUnknownModels"/> indicates
    /// whether at least one model in <paramref name="info"/> was missing
    /// from the catalog.
    /// </remarks>
    EstimateResult? Estimate(SessionModelInfo? info);
}

/// <summary>
/// Cost estimate returned by <see cref="IModelCostCalculator"/>.
/// </summary>
/// <param name="UsdAmount">Total estimated cost in US dollars.</param>
/// <param name="HasUnknownModels">
/// <see langword="true"/> when one or more models in the source usage map
/// were not in the catalog — the estimate is therefore a lower bound.
/// </param>
public sealed record EstimateResult(decimal UsdAmount, bool HasUnknownModels);
