using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CopilotSessionManager.Core.Models;
using CopilotSessionManager.Core.Sessions;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CopilotSessionManager.Core.Tests.Merge;

/// <summary>
/// Targeted coverage for <see cref="SessionReadmeService.AppendAsync"/>,
/// the one-line addition that the merge engine relies on. Lives under the
/// Merge folder so the merge engine's contract is testable end-to-end from
/// one place.
/// </summary>
public class SessionReadmeServiceAppendTests : IDisposable
{
    private readonly string _root;

    public SessionReadmeServiceAppendTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "csm-readme-append-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    private SessionReadmeService BuildSut()
    {
        var folders = new FakeFolderReader(_root);
        var store = new FileSessionReadmeStore(folders, NullLogger<FileSessionReadmeStore>.Instance);
        return new SessionReadmeService(
            new NoopRenderer(),
            store,
            folders,
            NullLogger<SessionReadmeService>.Instance);
    }

    [Fact]
    public async Task AppendAsync_NoExistingFile_CreatesItWithMarkdown()
    {
        var sut = BuildSut();
        await sut.AppendAsync("abc", "## Hello\n");

        var path = Path.Combine(_root, "abc", FileSessionReadmeStore.FileName);
        File.Exists(path).Should().BeTrue();
        (await File.ReadAllTextAsync(path)).Should().Be("## Hello\n");
    }

    [Fact]
    public async Task AppendAsync_AppendsToExistingContentWithSeparator()
    {
        var sut = BuildSut();
        var path = Path.Combine(_root, "abc", FileSessionReadmeStore.FileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "# Top\nbody line");

        await sut.AppendAsync("abc", "## Footer\n");

        var content = await File.ReadAllTextAsync(path);
        content.Should().StartWith("# Top\nbody line");
        content.Should().EndWith("## Footer\n");
        content.Should().Contain("body line\n\n## Footer", because: "blank line separates appended section from prior body");
    }

    [Fact]
    public async Task AppendAsync_PreservesTrailingNewlineWithSingleLeadingNewline()
    {
        var sut = BuildSut();
        var path = Path.Combine(_root, "abc", FileSessionReadmeStore.FileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "# Top\n");

        await sut.AppendAsync("abc", "## Footer\n");

        (await File.ReadAllTextAsync(path)).Should().Be("# Top\n\n## Footer\n");
    }

    [Fact]
    public async Task AppendAsync_NullMarkdown_Throws()
    {
        var sut = BuildSut();
        await FluentActions.Invoking(() => sut.AppendAsync("abc", null!))
            .Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task AppendAsync_BlankSessionId_Throws()
    {
        var sut = BuildSut();
        await FluentActions.Invoking(() => sut.AppendAsync(" ", "x"))
            .Should().ThrowAsync<ArgumentException>();
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

    private sealed class NoopRenderer : ISessionReadmeRenderer
    {
        public string Render(SessionReadmeContext context) => string.Empty;
    }
}
