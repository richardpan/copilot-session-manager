using System;
using System.IO;
using System.Threading.Tasks;
using CopilotSessionManager.Core.Models;
using CopilotSessionManager.Core.Sessions;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CopilotSessionManager.Core.Tests.Sessions;

public class JsonSessionLabelStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _filePath;

    public JsonSessionLabelStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "csm-labels-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _filePath = Path.Combine(_tempDir, "labels.json");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
            // best-effort cleanup; another test process may already have it.
        }
    }

    private JsonSessionLabelStore CreateSut() =>
        new(_filePath, NullLogger<JsonSessionLabelStore>.Instance);

    [Fact]
    public async Task GetAsync_MissingId_ReturnsExploratory()
    {
        var sut = CreateSut();
        (await sut.GetAsync("nope")).Should().Be(SessionType.Exploratory);
    }

    [Fact]
    public async Task SetAsync_PersistsAndIsReadableFromNewInstance()
    {
        var first = CreateSut();
        await first.SetAsync("abc", SessionType.Bug);
        await first.SetAsync("def", SessionType.Feature);

        File.Exists(_filePath).Should().BeTrue();

        var second = CreateSut();
        (await second.GetAsync("abc")).Should().Be(SessionType.Bug);
        (await second.GetAsync("def")).Should().Be(SessionType.Feature);
    }

    [Fact]
    public async Task SetAsync_ChangesLabel_RaisesLabelChangedOnce()
    {
        var sut = CreateSut();
        var fired = 0;
        SessionType? newType = null;
        sut.LabelChanged += (_, e) =>
        {
            fired++;
            newType = e.NewType;
        };

        await sut.SetAsync("abc", SessionType.Refactor);
        await sut.SetAsync("abc", SessionType.Refactor); // no-op
        await sut.SetAsync("abc", SessionType.Docs);     // change

        fired.Should().Be(2);
        newType.Should().Be(SessionType.Docs);
    }

    [Fact]
    public async Task RemoveAsync_RemovesEntryAndDefaultsBackToExploratory()
    {
        var sut = CreateSut();
        await sut.SetAsync("abc", SessionType.Bug);
        SessionType? raised = null;
        sut.LabelChanged += (_, e) => raised = e.NewType;

        await sut.RemoveAsync("abc");

        (await sut.GetAsync("abc")).Should().Be(SessionType.Exploratory);
        raised.Should().Be(SessionType.Exploratory);

        // From a fresh instance too — verify it persisted.
        var fresh = CreateSut();
        (await fresh.GetAllAsync()).Should().NotContainKey("abc");
    }

    [Fact]
    public async Task RemoveAsync_MissingId_DoesNothing()
    {
        var sut = CreateSut();
        var fired = 0;
        sut.LabelChanged += (_, _) => fired++;

        await sut.RemoveAsync("nope");

        fired.Should().Be(0);
    }

    [Fact]
    public async Task GetAllAsync_ReturnsOnlyExplicitEntries()
    {
        var sut = CreateSut();
        await sut.SetAsync("a", SessionType.Feature);
        await sut.SetAsync("b", SessionType.Infra);

        var all = await sut.GetAllAsync();

        all.Should().HaveCount(2);
        all["a"].Should().Be(SessionType.Feature);
        all["b"].Should().Be(SessionType.Infra);
    }

    [Fact]
    public async Task SessionId_LookupIsCaseInsensitive()
    {
        var sut = CreateSut();
        await sut.SetAsync("AbCdEf", SessionType.Experiment);

        (await sut.GetAsync("abcdef")).Should().Be(SessionType.Experiment);
        (await sut.GetAsync("ABCDEF")).Should().Be(SessionType.Experiment);
    }

    [Fact]
    public async Task CorruptFile_IsBackedUpAndStoreStartsFresh()
    {
        await File.WriteAllTextAsync(_filePath, "{ this is not valid json");

        var sut = CreateSut();
        // Get triggers a load; should not throw.
        (await sut.GetAsync("anything")).Should().Be(SessionType.Exploratory);

        var dirEntries = Directory.GetFiles(_tempDir);
        dirEntries.Should().Contain(p => Path.GetFileName(p).StartsWith("labels.json.bak."));
    }

    [Fact]
    public async Task PersistedDocument_HasVersionAndLabels()
    {
        var sut = CreateSut();
        await sut.SetAsync("abc", SessionType.Bug);

        var json = await File.ReadAllTextAsync(_filePath);
        json.Should().Contain("\"version\": 1");
        json.Should().Contain("\"abc\": \"Bug\"");
    }

    [Fact]
    public async Task NoTempFile_LeftBehindAfterSet()
    {
        var sut = CreateSut();
        await sut.SetAsync("abc", SessionType.Bug);

        File.Exists(_filePath + ".tmp").Should().BeFalse();
    }
}
