using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CopilotSessionManager.Core.Merge;
using CopilotSessionManager.Core.Models;
using CopilotSessionManager.Core.Sessions;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CopilotSessionManager.Core.Tests.Merge;

public class FileMergeImportWriterTests : IDisposable
{
    private readonly string _root;

    public FileMergeImportWriterTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "csm-merge-import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    private FileMergeImportWriter BuildSut(DateTimeOffset? now = null)
    {
        var clock = now is null ? TimeProvider.System : (TimeProvider)new FixedClock(now.Value);
        return new FileMergeImportWriter(
            new FakeFolderReader(_root),
            clock,
            NullLogger<FileMergeImportWriter>.Instance);
    }

    [Fact]
    public async Task WriteAsync_CreatesImportFolderAndFile()
    {
        var sut = BuildSut(new DateTimeOffset(2026, 5, 9, 17, 30, 4, TimeSpan.Zero));

        var path = await sut.WriteAsync("target-1", "source-2", "# Source transcript\nbody");

        path.Should().StartWith(Path.Combine(_root, "target-1", FileMergeImportWriter.ImportsFolderName));
        File.Exists(path).Should().BeTrue();
        Path.GetFileName(path).Should().Be("20260509T173004Z-from-source-2.md");
        (await File.ReadAllTextAsync(path)).Should().Contain("# Source transcript");
    }

    [Fact]
    public async Task WriteAsync_LeavesNoTempFile()
    {
        var sut = BuildSut();
        var path = await sut.WriteAsync("target-1", "src", "x");
        File.Exists(path + ".tmp").Should().BeFalse();
    }

    [Fact]
    public async Task WriteAsync_SanitizesSourceIdInFilename()
    {
        var sut = BuildSut(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

        var path = await sut.WriteAsync("target-1", "weird/id:with*chars", "data");

        var name = Path.GetFileName(path);
        // Invalid path chars must be replaced; timestamp prefix preserved.
        name.Should().StartWith("20260101T000000Z-from-");
        Path.GetInvalidFileNameChars().Should().NotContain(name.ToCharArray());
    }

    [Fact]
    public async Task WriteAsync_RejectsBlankIds()
    {
        var sut = BuildSut();

        await FluentActions.Invoking(() => sut.WriteAsync(" ", "src", "data"))
            .Should().ThrowAsync<ArgumentException>();
        await FluentActions.Invoking(() => sut.WriteAsync("target", "", "data"))
            .Should().ThrowAsync<ArgumentException>();
        await FluentActions.Invoking(() => sut.WriteAsync("target", "src", null!))
            .Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task WriteAsync_TwoCallsSameSecond_BothLandInSameFolder()
    {
        var fixedNow = new DateTimeOffset(2026, 5, 9, 12, 0, 0, TimeSpan.Zero);
        var sut = BuildSut(fixedNow);

        var p1 = await sut.WriteAsync("t", "src-a", "first");
        var p2 = await sut.WriteAsync("t", "src-b", "second");

        Path.GetDirectoryName(p1).Should().Be(Path.GetDirectoryName(p2));
        // Different source ids → different filenames so we don't clobber.
        p1.Should().NotBe(p2);
        (await File.ReadAllTextAsync(p1)).Should().Be("first");
        (await File.ReadAllTextAsync(p2)).Should().Be("second");
    }

    private sealed class FakeFolderReader : ISessionFolderReader
    {
        private readonly string _root;
        public FakeFolderReader(string root) => _root = root;
        public string GetSessionFolderPath(string sessionId) => Path.Combine(_root, sessionId);
        public Task<IReadOnlyList<SessionCheckpointSummary>> GetCheckpointsAsync(
            string sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SessionCheckpointSummary>>(Array.Empty<SessionCheckpointSummary>());
    }

    private sealed class FixedClock : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public FixedClock(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
