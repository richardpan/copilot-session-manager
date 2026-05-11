using System;
using System.IO;
using System.Threading.Tasks;
using CopilotSessionManager.Core.Sessions;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CopilotSessionManager.Core.Tests.Sessions;

/// <summary>
/// Tests for the per-session star store added with #112. Covers the
/// round-trip persistence guarantees (same-process and cross-process), the
/// idempotent set/remove contract, the StarsChanged event semantics, and
/// the corrupt-file recovery pattern shared with the display-name store.
/// </summary>
public class JsonSessionStarStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _filePath;

    public JsonSessionStarStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "csm-stars-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _filePath = Path.Combine(_tempDir, "stars.json");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
            /* best-effort */
        }
    }

    [Fact]
    public async Task IsStarred_DefaultsFalse_ForUnknownId()
    {
        var store = NewStore();

        (await store.IsStarredAsync("nope")).Should().BeFalse();
    }

    [Fact]
    public async Task SetAsync_PersistsAcrossInstances()
    {
        var store = NewStore();
        await store.SetAsync("abc");

        var reloaded = NewStore();
        (await reloaded.IsStarredAsync("abc")).Should().BeTrue();
    }

    [Fact]
    public async Task SetAsync_IsIdempotent_AndDoesNotRefireEvent()
    {
        var store = NewStore();
        var raised = 0;
        store.StarsChanged += (_, _) => raised++;

        await store.SetAsync("abc");
        await store.SetAsync("abc");

        raised.Should().Be(1);
    }

    [Fact]
    public async Task RemoveAsync_ClearsAndFiresEvent_OnlyWhenStateChanges()
    {
        var store = NewStore();
        await store.SetAsync("abc");

        var raised = 0;
        SessionStarChangedEventArgs? lastArgs = null;
        store.StarsChanged += (_, args) =>
        {
            raised++;
            lastArgs = args;
        };

        await store.RemoveAsync("abc");
        await store.RemoveAsync("abc"); // no-op

        raised.Should().Be(1);
        lastArgs!.SessionId.Should().Be("abc");
        lastArgs.IsStarred.Should().BeFalse();
        (await store.IsStarredAsync("abc")).Should().BeFalse();
    }

    [Fact]
    public async Task GetAllAsync_ReturnsAllStarredIds()
    {
        var store = NewStore();
        await store.SetAsync("a");
        await store.SetAsync("b");
        await store.SetAsync("c");
        await store.RemoveAsync("b");

        var all = await store.GetAllAsync();
        all.Should().BeEquivalentTo(new[] { "a", "c" });
    }

    [Fact]
    public async Task CorruptFile_IsBackedUpAndStoreStartsFresh()
    {
        await File.WriteAllTextAsync(_filePath, "{ this is not valid json");

        var store = NewStore();
        // First call materialises the cache; corrupt file should be tolerated.
        (await store.GetAllAsync()).Should().BeEmpty();

        // A backup sibling file should have been left next to the original.
        Directory.GetFiles(_tempDir, "stars.json.bak.*")
            .Should().NotBeEmpty();

        // Subsequent writes still succeed.
        await store.SetAsync("after-corrupt");
        (await store.IsStarredAsync("after-corrupt")).Should().BeTrue();
    }

    private JsonSessionStarStore NewStore() =>
        new(_filePath, NullLogger<JsonSessionStarStore>.Instance);
}
