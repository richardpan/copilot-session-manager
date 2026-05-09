using CopilotSessionManager.Core.Models;

namespace CopilotSessionManager.Core.Cli;

/// <summary>
/// Looks up <see cref="CopilotModel"/> metadata (display name, tier, rates)
/// from the model identifier the Copilot CLI emits in
/// <c>events.jsonl</c>.
/// </summary>
public interface IModelCatalog
{
    /// <summary>All models the catalog knows about.</summary>
    IReadOnlyList<CopilotModel> KnownModels { get; }

    /// <summary>
    /// Returns the catalog entry for <paramref name="modelId"/>, or
    /// <see langword="null"/> if the id is not recognized.
    /// </summary>
    CopilotModel? Resolve(string? modelId);
}
