using System.Collections.Frozen;
using CopilotSessionManager.Core.Models;

namespace CopilotSessionManager.Core.Cli;

/// <summary>
/// Hardcoded catalog of well-known Copilot models. Rates are USD per million
/// tokens and reflect public list pricing at the time of writing — they are
/// approximations meant to ground the dashboard's cost estimates, not to
/// match any individual user's contract. A user-overridable rate table is
/// tracked as a V2 follow-up.
/// </summary>
public sealed class EmbeddedModelCatalog : IModelCatalog
{
    private static readonly CopilotModel[] CatalogEntries = new[]
    {
        // ── Anthropic Claude (Premium tier) ──────────────────────────────
        new CopilotModel(
            Id: "claude-opus-4.7",
            DisplayName: "Opus 4.7",
            Tier: ModelTier.Premium,
            Rates: new ModelRates(15.00m, 75.00m, 1.50m, 18.75m, 75.00m)),
        new CopilotModel(
            Id: "claude-opus-4.6",
            DisplayName: "Opus 4.6",
            Tier: ModelTier.Premium,
            Rates: new ModelRates(15.00m, 75.00m, 1.50m, 18.75m, 75.00m)),
        new CopilotModel(
            Id: "claude-opus-4.5",
            DisplayName: "Opus 4.5",
            Tier: ModelTier.Premium,
            Rates: new ModelRates(15.00m, 75.00m, 1.50m, 18.75m, 75.00m)),

        // ── Anthropic Claude (Standard tier) ─────────────────────────────
        new CopilotModel(
            Id: "claude-sonnet-4.6",
            DisplayName: "Sonnet 4.6",
            Tier: ModelTier.Standard,
            Rates: new ModelRates(3.00m, 15.00m, 0.30m, 3.75m, 15.00m)),
        new CopilotModel(
            Id: "claude-sonnet-4.5",
            DisplayName: "Sonnet 4.5",
            Tier: ModelTier.Standard,
            Rates: new ModelRates(3.00m, 15.00m, 0.30m, 3.75m, 15.00m)),

        // ── Anthropic Claude (Fast tier) ────────────────────────────────
        new CopilotModel(
            Id: "claude-haiku-4.5",
            DisplayName: "Haiku 4.5",
            Tier: ModelTier.Fast,
            Rates: new ModelRates(1.00m, 5.00m, 0.10m, 1.25m, 5.00m)),

        // ── OpenAI GPT-5 family ─────────────────────────────────────────
        new CopilotModel(
            Id: "gpt-5.5",
            DisplayName: "GPT-5.5",
            Tier: ModelTier.Premium,
            Rates: new ModelRates(2.50m, 20.00m, 0.25m, 2.50m, 20.00m)),
        new CopilotModel(
            Id: "gpt-5.4",
            DisplayName: "GPT-5.4",
            Tier: ModelTier.Standard,
            Rates: new ModelRates(1.25m, 10.00m, 0.125m, 1.25m, 10.00m)),
        new CopilotModel(
            Id: "gpt-5.3-codex",
            DisplayName: "GPT-5.3 Codex",
            Tier: ModelTier.Standard,
            Rates: new ModelRates(1.25m, 10.00m, 0.125m, 1.25m, 10.00m)),
        new CopilotModel(
            Id: "gpt-5.2-codex",
            DisplayName: "GPT-5.2 Codex",
            Tier: ModelTier.Standard,
            Rates: new ModelRates(1.25m, 10.00m, 0.125m, 1.25m, 10.00m)),
        new CopilotModel(
            Id: "gpt-5.2",
            DisplayName: "GPT-5.2",
            Tier: ModelTier.Standard,
            Rates: new ModelRates(1.25m, 10.00m, 0.125m, 1.25m, 10.00m)),
        new CopilotModel(
            Id: "gpt-5.4-mini",
            DisplayName: "GPT-5.4 mini",
            Tier: ModelTier.Fast,
            Rates: new ModelRates(0.25m, 2.00m, 0.025m, 0.25m, 2.00m)),
        new CopilotModel(
            Id: "gpt-5-mini",
            DisplayName: "GPT-5 mini",
            Tier: ModelTier.Fast,
            Rates: new ModelRates(0.25m, 2.00m, 0.025m, 0.25m, 2.00m)),
        new CopilotModel(
            Id: "gpt-4.1",
            DisplayName: "GPT-4.1",
            Tier: ModelTier.Fast,
            Rates: new ModelRates(2.00m, 8.00m, 0.50m, 2.00m, 8.00m)),
    };

    private readonly FrozenDictionary<string, CopilotModel> _byId =
        CatalogEntries.ToFrozenDictionary(m => m.Id, StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    public IReadOnlyList<CopilotModel> KnownModels => CatalogEntries;

    /// <inheritdoc />
    public CopilotModel? Resolve(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
        {
            return null;
        }

        return _byId.TryGetValue(modelId, out var m) ? m : null;
    }
}
