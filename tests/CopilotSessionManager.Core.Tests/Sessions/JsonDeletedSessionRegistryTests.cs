using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CopilotSessionManager.Core.Sessions;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace CopilotSessionManager.Core.Tests.Sessions;

/// <summary>
/// Tests for the file-backed tombstone registry (#125).
/// </summary>
public class JsonDeletedSessionRegistryTests : IDisposable
{
    private readonly string _root;
    private readonly string _filePath;

    public JsonDeletedSessionRegistryTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "csm-tomb-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _filePath = Path.Combine(_root, JsonDeletedSessionRegistry.DefaultFileName);
    }

    public void Dispose()
    {
        try
        { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    private JsonDeletedSessionRegistry Sut() =>
        new(_filePath, NullLogger<JsonDeletedSessionRegistry>.Instance);

    [Fact]
    public async Task IsDeleted_emptyRegistry_returnsFalse()
    {
        (await Sut().IsDeletedAsync("abc")).Should().BeFalse();
    }

    [Fact]
    public async Task RecordAsync_marksAsDeletedAndPersists()
    {
        var sut1 = Sut();
        await sut1.RecordAsync("abc");
        (await sut1.IsDeletedAsync("abc")).Should().BeTrue();

        // New instance proves the on-disk persistence.
        var sut2 = Sut();
        (await sut2.IsDeletedAsync("abc")).Should().BeTrue();
    }

    [Fact]
    public async Task RecordAsync_isIdempotent()
    {
        var sut = Sut();
        await sut.RecordAsync("abc");
        await sut.RecordAsync("abc");
        (await sut.GetAllAsync()).Should().BeEquivalentTo(new[] { "abc" });
    }

    [Fact]
    public async Task RecordAsync_isCaseInsensitive()
    {
        var sut = Sut();
        await sut.RecordAsync("AAA");
        (await sut.IsDeletedAsync("aaa")).Should().BeTrue();
    }

    [Fact]
    public async Task ForgetAsync_removesTombstoneAndPersists()
    {
        var sut1 = Sut();
        await sut1.RecordAsync("abc");
        await sut1.ForgetAsync("abc");
        (await sut1.IsDeletedAsync("abc")).Should().BeFalse();

        var sut2 = Sut();
        (await sut2.IsDeletedAsync("abc")).Should().BeFalse();
    }

    [Fact]
    public async Task ForgetAsync_unknownId_isNoOp()
    {
        var sut = Sut();
        await sut.ForgetAsync("never-recorded");
        (await sut.GetAllAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task GetAllAsync_returnsSnapshot()
    {
        var sut = Sut();
        await sut.RecordAsync("a");
        await sut.RecordAsync("b");
        (await sut.GetAllAsync()).Should().BeEquivalentTo(new[] { "a", "b" });
    }

    [Fact]
    public async Task Load_corruptFile_isQuarantinedAndFreshRegistryStarted()
    {
        File.WriteAllText(_filePath, "{ this is not json");

        var sut = Sut();
        (await sut.GetAllAsync()).Should().BeEmpty();

        // The corrupt file should have been backed up, not deleted, so a
        // user can recover their tombstones if they need to.
        var backups = Directory.GetFiles(_root)
            .Where(f => Path.GetFileName(f).StartsWith(JsonDeletedSessionRegistry.DefaultFileName + ".bak.", StringComparison.Ordinal))
            .ToArray();
        backups.Should().HaveCount(1);
    }

    [Fact]
    public async Task RecordAsync_nullOrEmptyId_throws()
    {
        var sut = Sut();
        await Assert.ThrowsAsync<ArgumentException>(() => sut.RecordAsync(string.Empty));
        await Assert.ThrowsAsync<ArgumentException>(() => sut.RecordAsync("   "));
    }
}
