using CopilotSessionManager.Core.Cli;
using CopilotSessionManager.Core.Models;

namespace CopilotSessionManager.Core.Cost;

/// <summary>
/// Default <see cref="IModelCostCalculator"/> backed by <see cref="IModelCatalog"/>.
/// All arithmetic uses <see cref="decimal"/> to avoid binary rounding drift
/// across millions of tokens.
/// </summary>
public sealed class ModelCostCalculator : IModelCostCalculator
{
    private const decimal MillionTokens = 1_000_000m;

    private readonly IModelCatalog _catalog;

    public ModelCostCalculator(IModelCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _catalog = catalog;
    }

    /// <inheritdoc />
    public EstimateResult? Estimate(SessionModelInfo? info)
    {
        if (info is null || info.UsageByModel.Count == 0)
        {
            return null;
        }

        var total = 0m;
        var hasUnknown = false;

        foreach (var (modelId, usage) in info.UsageByModel)
        {
            var model = _catalog.Resolve(modelId);
            if (model is null)
            {
                hasUnknown = true;
                continue;
            }

            total += CostFor(usage, model.Rates);
        }

        return new EstimateResult(total, hasUnknown);
    }

    private static decimal CostFor(ModelUsage u, ModelRates r) =>
          u.InputTokens / MillionTokens * r.InputPerMillion
        + u.OutputTokens / MillionTokens * r.OutputPerMillion
        + u.CacheReadTokens / MillionTokens * r.CacheReadPerMillion
        + u.CacheWriteTokens / MillionTokens * r.CacheWritePerMillion
        + u.ReasoningTokens / MillionTokens * r.ReasoningPerMillion;
}
