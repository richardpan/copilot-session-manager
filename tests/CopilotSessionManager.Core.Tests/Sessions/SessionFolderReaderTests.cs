using System;
using System.IO;
using System.Threading.Tasks;
using CopilotSessionManager.Core.Sessions;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CopilotSessionManager.Core.Tests.Sessions;

public class SessionFolderReaderTests : IDisposable
{
    private readonly string _root;
    private readonly FakeCopilotPaths _paths;

    public SessionFolderReaderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "csm-folder-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _paths = new FakeCopilotPaths(_root);
    }

    public void Dispose()
    {
        try
        { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    private SessionFolderReader Sut() => new(_paths, NullLogger<SessionFolderReader>.Instance);

    [Fact]
    public void GetSessionFolderPath_CombinesIdWithSessionStateDir()
    {
        Sut().GetSessionFolderPath("abc").Should().Be(Path.Combine(_root, "abc"));
    }

    [Fact]
    public async Task GetCheckpointsAsync_NoFolder_ReturnsEmpty()
    {
        var result = await Sut().GetCheckpointsAsync("nope");
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetCheckpointsAsync_ReadsHeadingFromEachFile_AndOrdersByNumber()
    {
        var folder = Path.Combine(_root, "sess1", "checkpoints");
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "002-second.md"), "# Second checkpoint title\nbody");
        await File.WriteAllTextAsync(Path.Combine(folder, "001-first.md"), "# First checkpoint title\nbody");
        await File.WriteAllTextAsync(Path.Combine(folder, "010-tenth.md"), "no heading body only");

        var result = await Sut().GetCheckpointsAsync("sess1");

        result.Should().HaveCount(3);
        result[0].Number.Should().Be(1);
        result[0].Title.Should().Be("First checkpoint title");
        result[1].Number.Should().Be(2);
        result[1].Title.Should().Be("Second checkpoint title");
        result[2].Number.Should().Be(10);
        result[2].Title.Should().Be("010-tenth", because: "fallback to file stem when no heading is present");
    }

    [Fact]
    public async Task GetCheckpointsAsync_IgnoresUnnumberedFiles()
    {
        var folder = Path.Combine(_root, "sess2", "checkpoints");
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "README.md"), "# nope");
        await File.WriteAllTextAsync(Path.Combine(folder, "001-keep.md"), "# Keep");

        var result = await Sut().GetCheckpointsAsync("sess2");

        result.Should().HaveCount(1);
        result[0].Title.Should().Be("Keep");
    }

    private sealed class FakeCopilotPaths : ICopilotPaths
    {
        public FakeCopilotPaths(string root)
        {
            SessionStateDirectory = root;
            SessionStoreDatabasePath = Path.Combine(root, "session-store.db");
        }
        public string SessionStateDirectory { get; }
        public string SessionStoreDatabasePath { get; }
    }
}
