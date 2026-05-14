using System;
using System.IO;
using System.Threading.Tasks;
using CopilotSessionManager.Core.Sessions;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CopilotSessionManager.Core.Tests.Sessions;

/// <summary>
/// Tests for the V1.3 (#149) per-session wrap-up state store. Mirrors
/// <see cref="JsonSessionStarStoreTests"/> in shape: exercises the
/// round-trip persistence, the event semantics, and the corrupt-file
/// recovery pattern shared with the other JSON-backed stores.
/// </summary>
public class JsonWrapUpStateStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _filePath;

    public JsonWrapUpStateStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "csm-wrapup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _filePath = Path.Combine(_tempDir, "wrapup.json");
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
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task GetRequestedAt_ReturnsNull_ForUnknownId()
    {
        var store = NewStore();
        (await store.GetRequestedAtAsync("nope")).Should().BeNull();
    }

    [Fact]
    public async Task MarkRequested_PersistsAcrossInstances()
    {
        var ts = new DateTimeOffset(2026, 5, 8, 12, 0, 0, TimeSpan.Zero);
        var store = NewStore();
        await store.MarkRequestedAsync("abc", ts);

        var reloaded = NewStore();
        (await reloaded.GetRequestedAtAsync("abc")).Should().Be(ts);
    }

    [Fact]
    public async Task MarkRequested_FiresEvent_WithRequestedAtPayload()
    {
        var ts = new DateTimeOffset(2026, 5, 8, 12, 0, 0, TimeSpan.Zero);
        var store = NewStore();

        WrapUpStateChangedEventArgs? captured = null;
        store.WrapUpStateChanged += (_, args) => captured = args;

        await store.MarkRequestedAsync("abc", ts);

        captured.Should().NotBeNull();
        captured!.SessionId.Should().Be("abc");
        captured.RequestedAt.Should().Be(ts);
    }

    [Fact]
    public async Task ClearAsync_RemovesAndFiresEvent_OnlyWhenStateChanges()
    {
        var ts = new DateTimeOffset(2026, 5, 8, 12, 0, 0, TimeSpan.Zero);
        var store = NewStore();
        await store.MarkRequestedAsync("abc", ts);

        var raised = 0;
        WrapUpStateChangedEventArgs? lastArgs = null;
        store.WrapUpStateChanged += (_, args) =>
        {
            raised++;
            lastArgs = args;
        };

        await store.ClearAsync("abc");
        await store.ClearAsync("abc"); // no-op

        raised.Should().Be(1);
        lastArgs!.SessionId.Should().Be("abc");
        lastArgs.RequestedAt.Should().BeNull();
        (await store.GetRequestedAtAsync("abc")).Should().BeNull();
    }

    [Fact]
    public async Task GetAllAsync_ReturnsEverythingMarked()
    {
        var ts1 = new DateTimeOffset(2026, 5, 8, 12, 0, 0, TimeSpan.Zero);
        var ts2 = ts1.AddHours(1);
        var store = NewStore();
        await store.MarkRequestedAsync("a", ts1);
        await store.MarkRequestedAsync("b", ts2);
        await store.ClearAsync("a");

        var all = await store.GetAllAsync();
        all.Should().HaveCount(1);
        all["b"].Should().Be(ts2);
    }

    [Fact]
    public async Task CorruptFile_IsBackedUpAndStoreStartsFresh()
    {
        await File.WriteAllTextAsync(_filePath, "{ this is not valid json");

        var store = NewStore();
        (await store.GetAllAsync()).Should().BeEmpty();

        Directory.GetFiles(_tempDir, "wrapup.json.bak.*")
            .Should().NotBeEmpty();

        // Subsequent writes still succeed.
        var ts = DateTimeOffset.UtcNow;
        await store.MarkRequestedAsync("after-corrupt", ts);
        (await store.GetRequestedAtAsync("after-corrupt")).Should().Be(ts);
    }

    private JsonWrapUpStateStore NewStore() =>
        new(_filePath, NullLogger<JsonWrapUpStateStore>.Instance);
}
