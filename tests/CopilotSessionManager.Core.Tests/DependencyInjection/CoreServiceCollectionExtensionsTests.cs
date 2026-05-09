using CopilotSessionManager.Core.Cli;
using CopilotSessionManager.Core.Cli.Adapters.V1;
using CopilotSessionManager.Core.DependencyInjection;
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
    public void AddCopilotCliAdapters_is_idempotent()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));

        services.AddCopilotCliAdapters();
        services.AddCopilotCliAdapters();

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IEnumerable<ICopilotCliAdapter>>()
            .Should().ContainSingle();
    }
}
