using CopilotSessionManager.Core.Cli;
using CopilotSessionManager.Core.Cli.Adapters.V1;
using CopilotSessionManager.Core.Configuration;
using CopilotSessionManager.Core.Cost;
using CopilotSessionManager.Core.Sessions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace CopilotSessionManager.Core.DependencyInjection;

/// <summary>
/// DI registration helpers for the Core library.
/// </summary>
public static class CoreServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Copilot CLI adapter layer (interface + V1 adapter +
    /// registry). Safe to call multiple times.
    /// </summary>
    public static IServiceCollection AddCopilotCliAdapters(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<ICopilotCliAdapter, CopilotCliV1Adapter>());
        services.TryAddSingleton<ICopilotCliAdapterRegistry, CopilotCliAdapterRegistry>();
        services.TryAddSingleton<IModelCatalog, EmbeddedModelCatalog>();
        services.TryAddSingleton<IModelCostCalculator, ModelCostCalculator>();

        return services;
    }

    /// <summary>
    /// Registers the session discovery pipeline (paths, store, discovery
    /// service). Implies <see cref="AddCopilotCliAdapters"/>,
    /// <see cref="AddStatusDetection"/>, <see cref="AddSessionLabels"/>, and
    /// <see cref="AddSessionReadme"/>.
    /// </summary>
    public static IServiceCollection AddSessionDiscovery(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddCopilotCliAdapters();
        services.AddStatusDetection();
        services.AddSessionLabels();
        services.AddSessionReadme();
        services.TryAddSingleton<ICopilotPaths, DefaultCopilotPaths>();
        services.TryAddSingleton<ISessionStore, SessionStore>();
        services.TryAddSingleton<ISessionDiscoveryService, SessionDiscoveryService>();

        return services;
    }

    /// <summary>
    /// Registers <see cref="ISessionLabelStore"/> backed by
    /// <see cref="JsonSessionLabelStore"/> at
    /// <c>%LOCALAPPDATA%\CopilotSessionManager\labels.json</c>. Safe to call
    /// multiple times.
    /// </summary>
    public static IServiceCollection AddSessionLabels(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<ISessionLabelStore>(sp =>
        {
            var path = System.IO.Path.Combine(
                AppPaths.LocalAppDataDirectory,
                JsonSessionLabelStore.DefaultFileName);
            var logger = sp.GetRequiredService<ILogger<JsonSessionLabelStore>>();
            return new JsonSessionLabelStore(path, logger);
        });

        return services;
    }

    /// <summary>
    /// Registers the session README pipeline: folder reader, renderer,
    /// file-backed store, and orchestration service. Safe to call multiple
    /// times.
    /// </summary>
    public static IServiceCollection AddSessionReadme(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<ICopilotPaths, DefaultCopilotPaths>();
        services.TryAddSingleton<ISessionFolderReader, SessionFolderReader>();
        services.TryAddSingleton<ISessionReadmeRenderer>(_ => new TemplatedSessionReadmeRenderer());
        services.TryAddSingleton<ISessionReadmeStore, FileSessionReadmeStore>();
        services.TryAddSingleton<ISessionReadmeService, SessionReadmeService>();

        return services;
    }

    /// <summary>
    /// Registers the lock + events status detection pipeline used by
    /// <see cref="AddSessionDiscovery"/>. Safe to call multiple times. Pass
    /// <paramref name="configure"/> to tune <see cref="StatusDetectionOptions"/>.
    /// </summary>
    public static IServiceCollection AddStatusDetection(
        this IServiceCollection services,
        Action<StatusDetectionOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddCopilotCliAdapters();
        services.TryAddSingleton<ICopilotPaths, DefaultCopilotPaths>();
        services.TryAddSingleton<IProcessChecker, ProcessChecker>();
        services.TryAddSingleton<ISessionLockMonitor, SessionLockMonitor>();
        services.TryAddSingleton<ISessionStatusEvaluator, SessionStatusEvaluator>();

        if (configure is null)
        {
            services.TryAddSingleton(_ => new StatusDetectionOptions());
        }
        else
        {
            services.AddSingleton(_ =>
            {
                var options = new StatusDetectionOptions();
                configure(options);
                return options;
            });
        }

        return services;
    }
}
