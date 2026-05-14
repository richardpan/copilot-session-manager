using CopilotSessionManager.Core.Cli;
using CopilotSessionManager.Core.Cli.Adapters.V1;
using CopilotSessionManager.Core.Cli.Share;
using CopilotSessionManager.Core.Configuration;
using CopilotSessionManager.Core.Cost;
using CopilotSessionManager.Core.GitHub;
using CopilotSessionManager.Core.GitHub.Checks;
using CopilotSessionManager.Core.GitHub.Issues;
using CopilotSessionManager.Core.GitHub.Storage;
using CopilotSessionManager.Core.Logging;
using CopilotSessionManager.Core.Merge;
using CopilotSessionManager.Core.Onboarding;
using CopilotSessionManager.Core.Security;
using CopilotSessionManager.Core.Sessions;
using CopilotSessionManager.Core.Settings;
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
        services.AddSessionDocs();
        services.AddGitHubLinks();
        services.AddGitHubLinkStorage();
        services.AddSessionLifecycle();
        services.AddSubagentScanService();
        services.AddSessionEventSummary();
        services.TryAddSingleton<ICopilotPaths, DefaultCopilotPaths>();
        services.TryAddSingleton<ISessionStore, SessionStore>();
        services.TryAddSingleton<ISessionDiscoveryService>(sp =>
        {
            // We construct the concrete type so we can wire the optional
            // tombstone registry post-construction (#125) without
            // exploding the SessionDiscoveryService constructor surface.
            var concrete = ActivatorUtilities.CreateInstance<SessionDiscoveryService>(sp);
            concrete.SetDeletedSessionRegistry(sp.GetService<IDeletedSessionRegistry>());
            return concrete;
        });

        return services;
    }

    /// <summary>
    /// Registers the sub-agent scanner used by the dashboard row-details view.
    /// Safe to call multiple times.
    /// </summary>
    public static IServiceCollection AddSubagentScanService(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<ICopilotPaths, DefaultCopilotPaths>();
        services.TryAddSingleton<ISubagentScanService, SubagentScanService>();

        return services;
    }

    /// <summary>
    /// V1.3 (#144): Registers the events.jsonl summary scanner used by the
    /// README renderer to fill in the auto-generated activity sections
    /// (Recent prompts / Tool usage / Activity gaps). Safe to call multiple
    /// times.
    /// </summary>
    public static IServiceCollection AddSessionEventSummary(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<ICopilotPaths, DefaultCopilotPaths>();
        services.TryAddSingleton<ISessionEventSummaryService, SessionEventSummaryService>();

        return services;
    }

    /// <summary>
    /// Registers session-lifecycle services: stale lock cleanup and the
    /// external-PowerShell session launcher used by the "Resume" action on
    /// crashed sessions. Safe to call multiple times.
    /// </summary>
    public static IServiceCollection AddSessionLifecycle(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<ICopilotPaths, DefaultCopilotPaths>();
        services.TryAddSingleton<IProcessChecker, ProcessChecker>();
        services.TryAddSingleton<ISessionLockMonitor, SessionLockMonitor>();
        services.TryAddSingleton<ISessionLockCleanup, SessionLockCleanup>();
        services.TryAddSingleton<IProcessLauncher, ProcessLauncher>();
        services.TryAddSingleton<IPowerShellHostResolver, PathPowerShellHostResolver>();
        services.TryAddSingleton<ISessionLauncher, PowerShellSessionLauncher>();
        services.TryAddSingleton<IRunningSessionRegistry, InMemoryRunningSessionRegistry>();
        services.TryAddSingleton<ISessionFolderReader, SessionFolderReader>();
        services.AddSessionDisplayNames();
        services.AddSessionStars();
        services.AddDeletedSessionRegistry();
        services.TryAddSingleton<ISessionDeletionService, SessionDeletionService>();

        return services;
    }

    /// <summary>
    /// Registers <see cref="IDeletedSessionRegistry"/> backed by
    /// <see cref="JsonDeletedSessionRegistry"/> at
    /// <c>%LOCALAPPDATA%\CopilotSessionManager\deleted-sessions.json</c> so
    /// hard-deleted sessions (#106) stay deleted across rescans (#125)
    /// without csm having to write into Copilot CLI's
    /// <c>session-store.db</c> (ADR-002). Safe to call multiple times.
    /// </summary>
    public static IServiceCollection AddDeletedSessionRegistry(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IDeletedSessionRegistry>(sp =>
        {
            var path = System.IO.Path.Combine(
                AppPaths.LocalAppDataDirectory,
                JsonDeletedSessionRegistry.DefaultFileName);
            var logger = sp.GetRequiredService<ILogger<JsonDeletedSessionRegistry>>();
            return new JsonDeletedSessionRegistry(path, logger);
        });

        return services;
    }

    /// <summary>
    /// Registers <see cref="ISessionDisplayNameStore"/> backed by
    /// <see cref="JsonSessionDisplayNameStore"/> at
    /// <c>%LOCALAPPDATA%\CopilotSessionManager\display-names.json</c> for the
    /// inline rename feature (#105). Safe to call multiple times.
    /// </summary>
    public static IServiceCollection AddSessionDisplayNames(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<ISessionDisplayNameStore>(sp =>
        {
            var path = System.IO.Path.Combine(
                AppPaths.LocalAppDataDirectory,
                JsonSessionDisplayNameStore.DefaultFileName);
            var logger = sp.GetRequiredService<ILogger<JsonSessionDisplayNameStore>>();
            return new JsonSessionDisplayNameStore(path, logger);
        });

        return services;
    }

    /// <summary>
    /// Registers <see cref="ISessionStarStore"/> backed by
    /// <see cref="JsonSessionStarStore"/> at
    /// <c>%LOCALAPPDATA%\CopilotSessionManager\stars.json</c> for the
    /// per-session star / pin-to-top feature (#112). Safe to call multiple
    /// times.
    /// </summary>
    public static IServiceCollection AddSessionStars(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<ISessionStarStore>(sp =>
        {
            var path = System.IO.Path.Combine(
                AppPaths.LocalAppDataDirectory,
                JsonSessionStarStore.DefaultFileName);
            var logger = sp.GetRequiredService<ILogger<JsonSessionStarStore>>();
            return new JsonSessionStarStore(path, logger);
        });

        return services;
    }

    /// <summary>
    /// Registers CLI version probing used by the startup compatibility banner.
    /// Safe to call multiple times.
    /// </summary>
    public static IServiceCollection AddCliVersionProbe(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IProcessRunner, ProcessRunner>();
        services.TryAddSingleton(MinimumSupportedVersions.Default);
        services.TryAddSingleton<ICliVersionProbe, CliVersionProbe>();

        return services;
    }

    /// <summary>
    /// Registers the app-scoped CLI availability snapshot provider.
    /// Safe to call multiple times.
    /// </summary>
    public static IServiceCollection AddCliAvailability(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<ICliAvailabilityProvider, CliAvailabilityProvider>();
        services.AddCliVersionProbe();

        return services;
    }

    /// <summary>
    /// Registers first-run onboarding services: <see cref="IProcessRunner"/>,
    /// <see cref="IPrerequisiteChecker"/>, and <see cref="IAppSettingsStore"/>
    /// at <c>%LOCALAPPDATA%\CopilotSessionManager\settings.json</c>. Safe to
    /// call multiple times.
    /// </summary>
    public static IServiceCollection AddOnboarding(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<ICopilotPaths, DefaultCopilotPaths>();
        services.TryAddSingleton<IPowerShellHostResolver, PathPowerShellHostResolver>();
        services.TryAddSingleton<IProcessRunner, ProcessRunner>();
        services.TryAddSingleton<IPrerequisiteChecker, PrerequisiteChecker>();
        services.TryAddSingleton<IAppSettingsStore>(sp =>
        {
            var path = System.IO.Path.Combine(
                AppPaths.LocalAppDataDirectory,
                JsonAppSettingsStore.DefaultFileName);
            var logger = sp.GetRequiredService<ILogger<JsonAppSettingsStore>>();
            var migrations = sp.GetServices<IAppSettingsMigration>();
            return new JsonAppSettingsStore(path, logger, migrations);
        });

        return services;
    }

    /// <summary>
    /// App-wide DPAPI purpose string used by <see cref="AddSecurity"/> when
    /// it constructs the singleton <see cref="IDataProtector"/>. Bumping this
    /// effectively rotates every key protected through the registered
    /// protector — only do that when you intend to invalidate existing
    /// payloads.
    /// </summary>
    internal const string AppDbProtectorPurpose = "CopilotSessionManager.AppDb.v1";

    /// <summary>
    /// Registers <see cref="IDataProtector"/> as a singleton
    /// <see cref="DpapiDataProtector"/> bound to the app-wide purpose
    /// <c>"CopilotSessionManager.AppDb.v1"</c> and
    /// <see cref="DataProtectionScope.CurrentUser"/>. Infrastructure for
    /// ADR-0004 (DPAPI-protected app DB key); the actual SQLite consumer
    /// will be wired up when the V1 schema lands. Safe to call multiple
    /// times.
    /// </summary>
    public static IServiceCollection AddSecurity(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IDataProtector>(
            _ => new DpapiDataProtector(AppDbProtectorPurpose));

        return services;
    }

    /// <summary>
    /// Registers logging-support services that don't depend on Serilog itself
    /// (the WPF host owns Serilog wiring). Currently registers
    /// <see cref="ILogBundler"/> for the "Bundle logs for bug report"
    /// action. Safe to call multiple times.
    /// </summary>
    public static IServiceCollection AddLogging(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<ILogBundler, ZipLogBundler>();

        return services;
    }

    /// <summary>
    /// Registers the GitHub link resolver + <c>gh</c>-CLI–backed pull request
    /// lookup, plus the <see cref="IGitHubAvailabilityProvider"/> used to
    /// surface offline / unauthenticated state to view models. Safe to call
    /// multiple times.
    /// </summary>
    public static IServiceCollection AddGitHubLinks(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // GhCliGitHubClient now delegates to IProcessRunner; register a
        // default so callers don't have to also call AddOnboarding.
        services.TryAddSingleton<IProcessRunner, ProcessRunner>();
        services.TryAddSingleton<IGitHubAvailabilityProvider, GitHubAvailabilityProvider>();
        services.TryAddSingleton<IGitHubLinkResolver, GitHubLinkResolver>();
        services.TryAddSingleton<IGitHubClient, GhCliGitHubClient>();
        services.TryAddSingleton<IGitHubChecksClient, GhCliGitHubChecksClient>();

        return services;
    }

    /// <summary>
    /// Registers <see cref="ISessionGitHubLinksStore"/> backed by
    /// <see cref="JsonSessionGitHubLinksStore"/>. Persists user-supplied
    /// repository / branch / pull-request overrides per session at
    /// <c>&lt;sessionFolder&gt;/github-overrides.json</c> so they survive an
    /// app restart. Always-on; no settings. Safe to call multiple times.
    /// </summary>
    public static IServiceCollection AddGitHubLinkStorage(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<ICopilotPaths, DefaultCopilotPaths>();
        services.TryAddSingleton<ISessionFolderReader, SessionFolderReader>();
        services.TryAddSingleton<ISessionGitHubLinksStore, JsonSessionGitHubLinksStore>();

        return services;
    }

    /// <summary>
    /// Registers the manual GitHub-issue linking client
    /// (<see cref="IGitHubIssuesClient"/>) backed by
    /// <see cref="GhCliGitHubIssuesClient"/>. Implies the <c>gh</c>-CLI
    /// dependencies registered by <see cref="AddGitHubLinks"/>
    /// (<see cref="IProcessRunner"/> and
    /// <see cref="IGitHubAvailabilityProvider"/>) so call sites can use this
    /// extension on its own. Safe to call multiple times.
    /// </summary>
    public static IServiceCollection AddGitHubIssues(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IProcessRunner, ProcessRunner>();
        services.TryAddSingleton<IGitHubAvailabilityProvider, GitHubAvailabilityProvider>();
        services.TryAddSingleton<IGitHubIssuesClient, GhCliGitHubIssuesClient>();

        // README scanning (#71). Depends on the README store registered by
        // AddSessionReadme(); pull that in so this extension is self-contained
        // and can be called on its own without callers having to remember the
        // ordering.
        services.AddSessionReadme();
        services.TryAddSingleton<IReadmeIssueRefProvider, ReadmeIssueRefProvider>();

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
        services.TryAddSingleton<IDocFreshnessService, DocFreshnessService>();

        return services;
    }

    /// <summary>
    /// V1.6 (#118): Registers the brand-new csm-owned <c>SESSION-DOCS.md</c>
    /// scaffold + <c>SESSION-DOCS.html</c> generator. Distinct from
    /// <see cref="AddSessionReadme"/> — csm never overwrites SESSION-DOCS.md
    /// after first scaffold; users and agents own the file's content.
    /// Safe to call multiple times.
    /// </summary>
    public static IServiceCollection AddSessionDocs(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<ICopilotPaths, DefaultCopilotPaths>();
        services.TryAddSingleton<ISessionFolderReader, SessionFolderReader>();
        services.TryAddSingleton<ISessionDocsService, SessionDocsService>();

        return services;
    }

    /// <summary>
    /// Registers the session merge pipeline: <see cref="ICopilotShareInvoker"/>
    /// (wraps <c>copilot --share</c>), <see cref="IMergeImportWriter"/>, and
    /// <see cref="ISessionMerger"/>. Implies <see cref="AddOnboarding"/>
    /// (for <see cref="IProcessRunner"/>) and <see cref="AddSessionReadme"/>.
    /// Safe to call multiple times.
    /// </summary>
    public static IServiceCollection AddSessionMerge(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Both dependencies are needed; calling them is idempotent.
        services.AddOnboarding();
        services.AddSessionReadme();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ICopilotShareInvoker, CopilotShareInvoker>();
        services.TryAddSingleton<IMergeImportWriter, FileMergeImportWriter>();
        services.TryAddSingleton<ISessionMerger, SessionMerger>();

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
