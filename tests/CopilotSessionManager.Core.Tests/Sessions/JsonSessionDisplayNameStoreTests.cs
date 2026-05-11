using System;
using System.IO;
using System.Threading.Tasks;
using CopilotSessionManager.Core.Sessions;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CopilotSessionManager.Core.Tests.Sessions;

/// <summary>
/// Tests for the per-session display-name override store added with #105.
/// Mirrors the <see cref="JsonSessionLabelStoreTests"/> coverage so we know
/// the shared persistence patterns (atomic write, in-memory cache, corrupt
/// file recovery) are exercised on the new store too.
/// </summary>
public class JsonSessionDisplayNameStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _filePath;

    public JsonSessionDisplayNameStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "csm-displayname-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _filePath = Path.Combine(_tempDir, "display-names.json");
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

    private JsonSessionDisplayNameStore CreateSut() =>
        new(_filePath, NullLogger<JsonSessionDisplayNameStore>.Instance);

    [Fact]
    public async Task GetAsync_MissingId_ReturnsNull()
    {
        var sut = CreateSut();
        (await sut.GetAsync("missing")).Should().BeNull();
    }

    [Fact]
    public async Task SetAsync_PersistsAndIsReadableFromNewInstance()
    {
        var first = CreateSut();
        await first.SetAsync("abc", "My display name");
        await first.SetAsync("def", "Another");

        File.Exists(_filePath).Should().BeTrue();

        var second = CreateSut();
        (await second.GetAsync("abc")).Should().Be("My display name");
        (await second.GetAsync("def")).Should().Be("Another");
    }

    [Fact]
    public async Task SetAsync_TrimsWhitespace()
    {
        var sut = CreateSut();
        await sut.SetAsync("abc", "   spaced out   ");
        (await sut.GetAsync("abc")).Should().Be("spaced out");
    }

    [Fact]
    public async Task SetAsync_EmptyOrWhitespace_RemovesOverride()
    {
        var sut = CreateSut();
        await sut.SetAsync("abc", "Original");
        await sut.SetAsync("abc", "   ");
        (await sut.GetAsync("abc")).Should().BeNull();

        await sut.SetAsync("def", "Another");
        await sut.SetAsync("def", string.Empty);
        (await sut.GetAsync("def")).Should().BeNull();
    }

    [Fact]
    public async Task RemoveAsync_DropsEntry()
    {
        var sut = CreateSut();
        await sut.SetAsync("abc", "Foo");
        await sut.RemoveAsync("abc");
        (await sut.GetAsync("abc")).Should().BeNull();
    }

    [Fact]
    public async Task SetAsync_RaisesDisplayNameChanged()
    {
        var sut = CreateSut();
        SessionDisplayNameChangedEventArgs? captured = null;
        sut.DisplayNameChanged += (_, e) => captured = e;

        await sut.SetAsync("abc", "Foo");

        captured.Should().NotBeNull();
        captured!.SessionId.Should().Be("abc");
        captured.NewDisplayName.Should().Be("Foo");
    }

    [Fact]
    public async Task RemoveAsync_RaisesDisplayNameChangedWithNull()
    {
        var sut = CreateSut();
        await sut.SetAsync("abc", "Foo");

        SessionDisplayNameChangedEventArgs? captured = null;
        sut.DisplayNameChanged += (_, e) => captured = e;
        await sut.RemoveAsync("abc");

        captured.Should().NotBeNull();
        captured!.SessionId.Should().Be("abc");
        captured.NewDisplayName.Should().BeNull();
    }

    [Fact]
    public async Task SetAsync_NoChange_DoesNotRaiseEvent()
    {
        var sut = CreateSut();
        await sut.SetAsync("abc", "Foo");

        var fired = false;
        sut.DisplayNameChanged += (_, _) => fired = true;
        await sut.SetAsync("abc", "Foo");

        fired.Should().BeFalse();
    }

    [Fact]
    public async Task LoadAsync_CorruptFile_BacksUpAndStartsEmpty()
    {
        await File.WriteAllTextAsync(_filePath, "{ this is not valid json");
        var sut = CreateSut();

        (await sut.GetAsync("anything")).Should().BeNull();
        Directory.GetFiles(_tempDir, "display-names.json.bak.*")
            .Should().NotBeEmpty("corrupt files are backed up before being replaced");
    }

    [Fact]
    public async Task GetAllAsync_ReturnsCopy_NotLiveCache()
    {
        var sut = CreateSut();
        await sut.SetAsync("abc", "Foo");
        var snapshot = await sut.GetAllAsync();
        snapshot.Should().ContainKey("abc");

        await sut.SetAsync("def", "Bar");
        snapshot.Should().NotContainKey("def",
            "the snapshot should be a defensive copy, immune to subsequent writes");
    }
}
