using System;
using System.IO;
using System.Threading.Tasks;
using CopilotSessionManager.Core.Sessions;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CopilotSessionManager.Core.Tests.Sessions;

public class FileSessionReadmeStoreTests : IDisposable
{
    private readonly string _root;

    public FileSessionReadmeStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "csm-readme-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    private FileSessionReadmeStore Sut() =>
        new(
            new FakeFolderReader(_root),
            NullLogger<FileSessionReadmeStore>.Instance);

    [Fact]
    public void GetReadmePath_PutsFileInsideSessionFolder()
    {
        var path = Sut().GetReadmePath("abc");
        path.Should().Be(Path.Combine(_root, "abc", FileSessionReadmeStore.FileName));
    }

    [Fact]
    public async Task Exists_FalseWhenMissing_TrueAfterWrite()
    {
        var sut = Sut();
        sut.Exists("abc").Should().BeFalse();
        await sut.WriteAsync("abc", "# hi\n");
        sut.Exists("abc").Should().BeTrue();
    }

    [Fact]
    public async Task WriteAsync_CreatesFolder_AndWritesFile()
    {
        var sut = Sut();
        await sut.WriteAsync("abc", "# hi\n");

        var path = sut.GetReadmePath("abc");
        File.Exists(path).Should().BeTrue();
        (await File.ReadAllTextAsync(path)).Should().Be("# hi\n");
    }

    [Fact]
    public async Task WriteAsync_LeavesNoTempFileBehind()
    {
        var sut = Sut();
        await sut.WriteAsync("abc", "# hi\n");
        File.Exists(sut.GetReadmePath("abc") + ".tmp").Should().BeFalse();
    }

    [Fact]
    public async Task WriteAsync_RaisesReadmeChanged()
    {
        var sut = Sut();
        SessionReadmeChangedEventArgs? captured = null;
        sut.ReadmeChanged += (_, e) => captured = e;

        await sut.WriteAsync("abc", "# hi\n");

        captured.Should().NotBeNull();
        captured!.SessionId.Should().Be("abc");
        captured.Path.Should().Be(sut.GetReadmePath("abc"));
    }

    [Fact]
    public async Task WriteAsync_PreservesUserBlock_AcrossRegeneration()
    {
        var sut = Sut();

        const string firstRender =
            "# Title\n## Notes\n<!-- USER:BEGIN notes -->\n_placeholder_\n<!-- USER:END notes -->\n";
        await sut.WriteAsync("abc", firstRender);

        // Simulate user editing the body of the user block.
        var path = sut.GetReadmePath("abc");
        var edited = (await File.ReadAllTextAsync(path))
            .Replace("_placeholder_\n", "My handcrafted notes line 1\nLine 2\n", StringComparison.Ordinal);
        await File.WriteAllTextAsync(path, edited);

        const string secondRender =
            "# Title v2\n## Notes\n<!-- USER:BEGIN notes -->\n_new placeholder_\n<!-- USER:END notes -->\n";
        var merged = await sut.WriteAsync("abc", secondRender);

        merged.Should().Contain("My handcrafted notes line 1");
        merged.Should().Contain("Line 2");
        merged.Should().NotContain("_new placeholder_");
        merged.Should().Contain("# Title v2", because: "auto sections regenerate freely");
    }

    [Fact]
    public async Task WriteAsync_PreservesMultipleUserBlocks()
    {
        var sut = Sut();
        const string first =
            "## Notes\n<!-- USER:BEGIN notes -->\nA\n<!-- USER:END notes -->\n" +
            "## Next\n<!-- USER:BEGIN next-steps -->\nB\n<!-- USER:END next-steps -->\n";
        await sut.WriteAsync("abc", first);

        var path = sut.GetReadmePath("abc");
        var edited = (await File.ReadAllTextAsync(path))
            .Replace("A\n", "AA\n", StringComparison.Ordinal)
            .Replace("B\n", "BB\n", StringComparison.Ordinal);
        await File.WriteAllTextAsync(path, edited);

        const string second =
            "## Notes\n<!-- USER:BEGIN notes -->\nplaceholder\n<!-- USER:END notes -->\n" +
            "## Next\n<!-- USER:BEGIN next-steps -->\nplaceholder\n<!-- USER:END next-steps -->\n";
        var merged = await sut.WriteAsync("abc", second);

        merged.Should().Contain("AA");
        merged.Should().Contain("BB");
    }

    [Fact]
    public async Task WriteAsync_NewBlock_PassesThroughWhenNoExistingMatch()
    {
        var sut = Sut();
        await sut.WriteAsync(
            "abc",
            "## Notes\n<!-- USER:BEGIN notes -->\noriginal\n<!-- USER:END notes -->\n");

        var merged = await sut.WriteAsync(
            "abc",
            "## Notes\n<!-- USER:BEGIN notes -->\nA\n<!-- USER:END notes -->\n" +
            "## Next\n<!-- USER:BEGIN next-steps -->\nfresh\n<!-- USER:END next-steps -->\n");

        merged.Should().Contain("original");
        merged.Should().Contain("fresh", because: "blocks not present in prior file fall through unchanged");
    }

    [Fact]
    public async Task ReadAsync_MissingFile_ReturnsNull()
    {
        var sut = Sut();
        (await sut.ReadAsync("nope")).Should().BeNull();
    }

    private sealed class FakeFolderReader : ISessionFolderReader
    {
        private readonly string _root;
        public FakeFolderReader(string root) => _root = root;
        public string GetSessionFolderPath(string sessionId) => Path.Combine(_root, sessionId);
        public Task<System.Collections.Generic.IReadOnlyList<CopilotSessionManager.Core.Models.SessionCheckpointSummary>>
            GetCheckpointsAsync(string sessionId, System.Threading.CancellationToken cancellationToken = default) =>
            Task.FromResult<System.Collections.Generic.IReadOnlyList<CopilotSessionManager.Core.Models.SessionCheckpointSummary>>(
                Array.Empty<CopilotSessionManager.Core.Models.SessionCheckpointSummary>());
    }
}
