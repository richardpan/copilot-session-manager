using CopilotSessionManager.Core.Cli;
using CopilotSessionManager.Core.Cli.Adapters.V1;
using CopilotSessionManager.Core.DependencyInjection;
using CopilotSessionManager.Core.Security;
using CopilotSessionManager.Core.Sessions;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CopilotSessionManager.Core.Tests.DependencyInjection;

public class CoreServiceCollectionExtensionsTests
{
    [Fact]
    public void AddCopilotCliAdapters_registers_v1_adapter_and_registry()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));

        services.AddCopilotCliAdapters();

        using var provider = services.BuildServiceProvider();

        var adapters = provider.GetRequiredService<IEnumerable<ICopilotCliAdapter>>().ToList();
        adapters.Should().ContainSingle().Which.Should().BeOfType<CopilotCliV1Adapter>();

        var registry = provider.GetRequiredService<ICopilotCliAdapterRegistry>();
        registry.Adapters.Should().ContainSingle();
    }

    [Fact]
    public async Task AddSessionDiscovery_registers_paths_store_and_discovery()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));

        services.AddSessionDiscovery();

        await using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<ICopilotPaths>().Should().BeOfType<DefaultCopilotPaths>();
        provider.GetRequiredService<ISessionStore>().Should().BeOfType<SessionStore>();
        provider.GetRequiredService<ISessionDiscoveryService>().Should().BeOfType<SessionDiscoveryService>();
        provider.GetRequiredService<ICopilotCliAdapterRegistry>().Should().NotBeNull();
        provider.GetRequiredService<IProcessChecker>().Should().BeOfType<ProcessChecker>();
        provider.GetRequiredService<ISessionLockMonitor>().Should().BeOfType<SessionLockMonitor>();
        provider.GetRequiredService<ISessionStatusEvaluator>().Should().BeOfType<SessionStatusEvaluator>();
        provider.GetRequiredService<StatusDetectionOptions>().Should().NotBeNull();
    }

    [Fact]
    public async Task AddStatusDetection_honors_options_configurator()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));

        services.AddStatusDetection(options => options.IdleThreshold = TimeSpan.FromMinutes(42));

        await using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<StatusDetectionOptions>().IdleThreshold
            .Should().Be(TimeSpan.FromMinutes(42));
    }

    [Fact]
    public async Task AddSecurity_registers_singleton_dpapi_data_protector()
    {
        var services = new ServiceCollection();

        services.AddSecurity();
        services.AddSecurity(); // idempotent — TryAddSingleton

        await using var provider = services.BuildServiceProvider();

        var first = provider.GetRequiredService<IDataProtector>();
        var second = provider.GetRequiredService<IDataProtector>();

        first.Should().BeOfType<DpapiDataProtector>();
        second.Should().BeSameAs(first);
    }
}
