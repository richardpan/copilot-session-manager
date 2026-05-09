using CopilotSessionManager.Core.Cli;
using CopilotSessionManager.Core.Models;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace CopilotSessionManager.Core.Tests.Cli;

public class CopilotCliAdapterRegistryTests
{
    [Fact]
    public void Constructor_throws_when_no_adapters_registered()
    {
        var act = () => new CopilotCliAdapterRegistry(
            Array.Empty<ICopilotCliAdapter>(),
            NullLogger<CopilotCliAdapterRegistry>.Instance);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Latest_is_the_adapter_with_the_highest_max_supported()
    {
        var v1 = new FakeAdapter(new(1, 0, 0), new(1, 99, 99));
        var v2 = new FakeAdapter(new(2, 0, 0), new(2, 99, 99));

        var registry = new CopilotCliAdapterRegistry(
            new ICopilotCliAdapter[] { v1, v2 },
            NullLogger<CopilotCliAdapterRegistry>.Instance);

        registry.Latest.Should().BeSameAs(v2);
        registry.Adapters.Should().HaveCount(2);
        registry.Adapters[0].Should().BeSameAs(v2);
    }

    [Fact]
    public void Resolve_returns_matching_adapter_when_one_exists()
    {
        var v1 = new FakeAdapter(new(1, 0, 0), new(1, 99, 99));
        var v2 = new FakeAdapter(new(2, 0, 0), new(2, 99, 99));

        var registry = new CopilotCliAdapterRegistry(
            new ICopilotCliAdapter[] { v1, v2 },
            NullLogger<CopilotCliAdapterRegistry>.Instance);

        var resolution = registry.Resolve(new CopilotVersion(1, 5, 7));

        resolution.IsFallback.Should().BeFalse();
        resolution.Adapter.Should().BeSameAs(v1);
    }

    [Fact]
    public void Resolve_falls_back_to_latest_when_no_adapter_matches()
    {
        var v1 = new FakeAdapter(new(1, 0, 0), new(1, 99, 99));
        var v2 = new FakeAdapter(new(2, 0, 0), new(2, 99, 99));

        var registry = new CopilotCliAdapterRegistry(
            new ICopilotCliAdapter[] { v1, v2 },
            NullLogger<CopilotCliAdapterRegistry>.Instance);

        var resolution = registry.Resolve(new CopilotVersion(3, 0, 0));

        resolution.IsFallback.Should().BeTrue();
        resolution.Adapter.Should().BeSameAs(v2);
    }

    private sealed class FakeAdapter : ICopilotCliAdapter
    {
        public FakeAdapter(CopilotVersion min, CopilotVersion max)
        {
            MinSupported = min;
            MaxSupported = max;
        }

        public CopilotVersion MinSupported { get; }
        public CopilotVersion MaxSupported { get; }

        public bool Supports(CopilotVersion version) =>
            version >= MinSupported && version <= MaxSupported;

        public Task<CopilotVersion?> ReadCopilotVersionAsync(Stream eventsJsonl, CancellationToken cancellationToken = default) =>
            Task.FromResult<CopilotVersion?>(null);

        public IAsyncEnumerable<SessionEvent> ParseEventsAsync(Stream eventsJsonl, CancellationToken cancellationToken = default) =>
            AsyncEnumerable.Empty<SessionEvent>();

        public WorkspaceManifest ParseWorkspace(string yaml) =>
            throw new NotImplementedException();
    }

    private static class AsyncEnumerable
    {
        public static async IAsyncEnumerable<T> Empty<T>()
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
