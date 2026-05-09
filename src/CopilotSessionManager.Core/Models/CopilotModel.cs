namespace CopilotSessionManager.Core.Models;

/// <summary>
/// A Copilot model surfaced by the embedded catalog: stable id (matches the
/// id Copilot CLI emits in <c>events.jsonl</c>), human-friendly display name,
/// coarse <see cref="ModelTier"/>, and per-token cost rates.
/// </summary>
public sealed record CopilotModel(
    string Id,
    string DisplayName,
    ModelTier Tier,
    ModelRates Rates);
