using CopilotSessionManager.Core.Models;
using Microsoft.Extensions.Logging;

namespace CopilotSessionManager.Core.Cli;

/// <inheritdoc />
public sealed class CopilotCliAdapterRegistry : ICopilotCliAdapterRegistry
{
    private readonly ILogger<CopilotCliAdapterRegistry> _logger;

    public CopilotCliAdapterRegistry(
        IEnumerable<ICopilotCliAdapter> adapters,
        ILogger<CopilotCliAdapterRegistry> logger)
    {
        ArgumentNullException.ThrowIfNull(adapters);
        ArgumentNullException.ThrowIfNull(logger);

        _logger = logger;

        Adapters = adapters
            .OrderByDescending(a => a.MaxSupported)
            .ThenByDescending(a => a.MinSupported)
            .ToArray();

        if (Adapters.Count == 0)
        {
            throw new InvalidOperationException(
                "At least one ICopilotCliAdapter must be registered.");
        }
    }

    public ICopilotCliAdapter Latest => Adapters[0];

    public IReadOnlyList<ICopilotCliAdapter> Adapters { get; }

    public AdapterResolution Resolve(CopilotVersion version)
    {
        foreach (var adapter in Adapters)
        {
            if (adapter.Supports(version))
            {
                return new AdapterResolution(adapter, IsFallback: false);
            }
        }

        _logger.LogWarning(
            "No registered adapter supports Copilot CLI {Version}; falling back to {Latest} ({MinSupported}-{MaxSupported}).",
            version,
            Latest.GetType().Name,
            Latest.MinSupported,
            Latest.MaxSupported);

        return new AdapterResolution(Latest, IsFallback: true);
    }
}
