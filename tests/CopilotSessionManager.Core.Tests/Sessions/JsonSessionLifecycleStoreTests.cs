using System;
using System.IO;
using System.Threading.Tasks;
using CopilotSessionManager.Core.Sessions;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CopilotSessionManager.Core.Tests.Sessions;

/// <summary>
/// Tests for the per-session lifecycle store backing the user-controlled
/// Active/Closed pill. Mirrors <see cref="JsonSessionStarStoreTests"/>:
/// round-trip persistence, idempotent set semantics, change-event firing
/// only on actual transitions, and corrupt-file recovery.
/// </summary>
public class JsonSessionLifecycleStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _filePath;

    public JsonSessionLifecycleStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "csm-lifecycle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _filePath = Path.Combine(_tempDir, "lifecycle.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task GetAsync_DefaultsActive_ForUnknownId()
    {
        var store = NewStore();
        (await store.GetAsync("nope")).Should().Be(SessionLifecycleState.Active);
    }

    [Fact]
    public async Task SetClosed_PersistsAcrossInstances()
    {
        var store = NewStore();
        await store.SetAsync("abc", SessionLifecycleState.Closed);

        var reloaded = NewStore();
        (await reloaded.GetAsync("abc")).Should().Be(SessionLifecycleState.Closed);
    }

    [Fact]
    public async Task SetClosed_IsIdempotent_AndDoesNotRefireEvent()
    {
        var store = NewStore();
        var raised = 0;
        store.LifecycleChanged += (_, _) => raised++;

        await store.SetAsync("abc", SessionLifecycleState.Closed);
        await store.SetAsync("abc", SessionLifecycleState.Closed);

        raised.Should().Be(1);
    }

    [Fact]
    public async Task SetActive_ClearsClosed_AndFiresEventOnlyWhenStateChanges()
    {
        var store = NewStore();
        await store.SetAsync("abc", SessionLifecycleState.Closed);

        var raised = 0;
        SessionLifecycleChangedEventArgs? lastArgs = null;
        store.LifecycleChanged += (_, args) =>
        {
            raised++;
            lastArgs = args;
        };

        await store.SetAsync("abc", SessionLifecycleState.Active);
        await store.SetAsync("abc", SessionLifecycleState.Active); // no-op

        raised.Should().Be(1);
        lastArgs!.SessionId.Should().Be("abc");
        lastArgs.State.Should().Be(SessionLifecycleState.Active);
        (await store.GetAsync("abc")).Should().Be(SessionLifecycleState.Active);
    }

    [Fact]
    public async Task GetClosedAsync_ReturnsAllExplicitlyClosedIds()
    {
        var store = NewStore();
        await store.SetAsync("a", SessionLifecycleState.Closed);
        await store.SetAsync("b", SessionLifecycleState.Closed);
        await store.SetAsync("c", SessionLifecycleState.Closed);
        await store.SetAsync("b", SessionLifecycleState.Active);

        var all = await store.GetClosedAsync();
        all.Should().BeEquivalentTo(new[] { "a", "c" });
    }

    [Fact]
    public async Task CorruptFile_IsBackedUpAndStoreStartsFresh()
    {
        await File.WriteAllTextAsync(_filePath, "{ this is not valid json");

        var store = NewStore();
        (await store.GetClosedAsync()).Should().BeEmpty();

        Directory.GetFiles(_tempDir, "lifecycle.json.bak.*")
            .Should().NotBeEmpty();

        await store.SetAsync("after-corrupt", SessionLifecycleState.Closed);
        (await store.GetAsync("after-corrupt")).Should().Be(SessionLifecycleState.Closed);
    }

    private JsonSessionLifecycleStore NewStore() =>
        new(_filePath, NullLogger<JsonSessionLifecycleStore>.Instance);
}
