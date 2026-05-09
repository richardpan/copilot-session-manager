using CopilotSessionManager.Core.Models;

namespace CopilotSessionManager.Core.Cli;

/// <summary>
/// Picks the right <see cref="ICopilotCliAdapter"/> for a given Copilot CLI
/// version. If no adapter matches, returns the most recent registered adapter
/// and signals via <see cref="AdapterResolution.IsFallback"/>.
/// </summary>
public interface ICopilotCliAdapterRegistry
{
    /// <summary>The most recent registered adapter (used as the fallback).</summary>
    ICopilotCliAdapter Latest { get; }

    /// <summary>All registered adapters, ordered newest-first by MaxSupported.</summary>
    IReadOnlyList<ICopilotCliAdapter> Adapters { get; }

    /// <summary>Resolve an adapter for <paramref name="version"/>.</summary>
    AdapterResolution Resolve(CopilotVersion version);
}

/// <summary>The outcome of an adapter lookup.</summary>
/// <param name="Adapter">The adapter chosen.</param>
/// <param name="IsFallback">
/// True if no registered adapter explicitly supports the requested version and
/// the latest adapter is being used as a best-effort fallback.
/// </param>
public sealed record AdapterResolution(ICopilotCliAdapter Adapter, bool IsFallback);
