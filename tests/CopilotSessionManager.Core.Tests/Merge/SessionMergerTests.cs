using System;
using System.Threading;
using System.Threading.Tasks;
using CopilotSessionManager.Core.Cli.Share;
using CopilotSessionManager.Core.Merge;
using CopilotSessionManager.Core.Models;
using CopilotSessionManager.Core.Sessions;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CopilotSessionManager.Core.Tests.Merge;

public class SessionMergerTests
{
    private static SessionMerger BuildSut(
        FakeShareInvoker share,
        FakeImportWriter importer,
        FakeReadme readme,
        DateTimeOffset? now = null) =>
        new(
            share,
            importer,
            readme,
            now is null ? TimeProvider.System : new FixedClock(now.Value),
            NullLogger<SessionMerger>.Instance);

    [Fact]
    public async Task MergeAsync_BlankSourceId_ReturnsFailure()
    {
        var sut = BuildSut(new FakeShareInvoker(), new FakeImportWriter(), new FakeReadme());
        var r = await sut.MergeAsync("", "target");
        r.Success.Should().BeFalse();
        r.ErrorMessage.Should().Contain("Source");
    }

    [Fact]
    public async Task MergeAsync_BlankTargetId_ReturnsFailure()
    {
        var sut = BuildSut(new FakeShareInvoker(), new FakeImportWriter(), new FakeReadme());
        var r = await sut.MergeAsync("source", "");
        r.Success.Should().BeFalse();
        r.ErrorMessage.Should().Contain("Target");
    }

    [Fact]
    public async Task MergeAsync_SameSourceAndTarget_ReturnsFailure()
    {
        var sut = BuildSut(new FakeShareInvoker(), new FakeImportWriter(), new FakeReadme());
        var r = await sut.MergeAsync("same", "same");
        r.Success.Should().BeFalse();
        r.ErrorMessage.Should().Contain("different");
    }

    [Fact]
    public async Task MergeAsync_FullSuccess_CallsAllThreeAndReturnsMergeNote()
    {
        var share = new FakeShareInvoker { Result = ShareResult.Ok("/tmp/x.md", "# Hi\nbody") };
        var importer = new FakeImportWriter();
        var readme = new FakeReadme();
        var when = new DateTimeOffset(2026, 5, 9, 17, 30, 4, TimeSpan.Zero);

        var sut = BuildSut(share, importer, readme, when);
        var r = await sut.MergeAsync("source-1", "target-2");

        r.Success.Should().BeTrue();
        r.ErrorMessage.Should().BeNull();
        r.MergeNote.Should().Contain("source-1");
        r.MergeNote.Should().Contain("2026-05-09 17:30:04 UTC");

        share.LastSourceId.Should().Be("source-1");
        importer.LastTargetId.Should().Be("target-2");
        importer.LastSourceId.Should().Be("source-1");
        importer.LastMarkdown.Should().Contain("# Hi");
        readme.LastSessionId.Should().Be("target-2");
        readme.LastMarkdown.Should().Contain("Merged from session");
    }

    [Fact]
    public async Task MergeAsync_ShareFails_SurfacesErrorAndSkipsImporter()
    {
        var share = new FakeShareInvoker { Result = ShareResult.Fail("CLI exploded") };
        var importer = new FakeImportWriter();
        var readme = new FakeReadme();
        var sut = BuildSut(share, importer, readme);

        var r = await sut.MergeAsync("source", "target");

        r.Success.Should().BeFalse();
        r.ErrorMessage.Should().Contain("export source");
        r.ErrorMessage.Should().Contain("CLI exploded");
        importer.LastTargetId.Should().BeNull();
        readme.LastSessionId.Should().BeNull();
    }

    [Fact]
    public async Task MergeAsync_ShareEmptyMarkdown_TreatedAsFailure()
    {
        // Defensive: ShareResult.Ok with empty markdown should still be handled.
        var share = new FakeShareInvoker { Result = new ShareResult(true, "/tmp/x.md", "", null) };
        var importer = new FakeImportWriter();
        var readme = new FakeReadme();
        var sut = BuildSut(share, importer, readme);

        var r = await sut.MergeAsync("source", "target");

        r.Success.Should().BeFalse();
        importer.LastTargetId.Should().BeNull();
    }

    [Fact]
    public async Task MergeAsync_ImporterThrows_SurfacesErrorAndSkipsReadme()
    {
        var share = new FakeShareInvoker { Result = ShareResult.Ok("/tmp/x.md", "data") };
        var importer = new FakeImportWriter { ThrowOnWrite = new IOException("disk full") };
        var readme = new FakeReadme();
        var sut = BuildSut(share, importer, readme);

        var r = await sut.MergeAsync("source", "target");

        r.Success.Should().BeFalse();
        r.ErrorMessage.Should().Contain("disk full");
        r.ErrorMessage.Should().Contain("merge import");
        readme.LastSessionId.Should().BeNull();
    }

    [Fact]
    public async Task MergeAsync_ReadmeAppendFails_StillReportsSuccessWithNullNote()
    {
        var share = new FakeShareInvoker { Result = ShareResult.Ok("/tmp/x.md", "data") };
        var importer = new FakeImportWriter();
        var readme = new FakeReadme { ThrowOnAppend = new IOException("readme locked") };
        var sut = BuildSut(share, importer, readme);

        var r = await sut.MergeAsync("source", "target");

        r.Success.Should().BeTrue(because: "the merge content has already been imported; README is best-effort");
        r.MergeNote.Should().BeNull();
        importer.LastTargetId.Should().Be("target");
    }

    [Fact]
    public async Task MergeAsync_Cancellation_PropagatesFromShareInvoker()
    {
        var share = new FakeShareInvoker { ThrowOnExport = new OperationCanceledException() };
        var sut = BuildSut(share, new FakeImportWriter(), new FakeReadme());

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await FluentActions.Invoking(() => sut.MergeAsync("source", "target", cts.Token))
            .Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void BuildMergeNote_FormatsTimestampAsUtc()
    {
        var note = SessionMerger.BuildMergeNote("abc", new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero));
        note.Should().Be("## Merged from session `abc` on 2026-01-02 03:04:05 UTC\n");
    }

    private sealed class FakeShareInvoker : ICopilotShareInvoker
    {
        public ShareResult Result { get; set; } = ShareResult.Fail("not configured");
        public Exception? ThrowOnExport { get; set; }
        public string? LastSourceId { get; private set; }

        public Task<ShareResult> ExportAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            LastSourceId = sessionId;
            if (ThrowOnExport is not null)
            {
                throw ThrowOnExport;
            }
            return Task.FromResult(Result);
        }
    }

    private sealed class FakeImportWriter : IMergeImportWriter
    {
        public Exception? ThrowOnWrite { get; set; }
        public string? LastTargetId { get; private set; }
        public string? LastSourceId { get; private set; }
        public string? LastMarkdown { get; private set; }

        public Task<string> WriteAsync(string targetSessionId, string sourceSessionId, string markdown, CancellationToken cancellationToken = default)
        {
            if (ThrowOnWrite is not null)
            {
                throw ThrowOnWrite;
            }
            LastTargetId = targetSessionId;
            LastSourceId = sourceSessionId;
            LastMarkdown = markdown;
            return Task.FromResult($"/fake/{targetSessionId}/imports/foo.md");
        }
    }

    private sealed class FakeReadme : ISessionReadmeService
    {
        public Exception? ThrowOnAppend { get; set; }
        public string? LastSessionId { get; private set; }
        public string? LastMarkdown { get; private set; }

        public string GetReadmePath(string sessionId) => $"/fake/{sessionId}/SESSION-README.md";

        public Task<string> EnsureAsync(Session session, SessionType label, CancellationToken cancellationToken = default) =>
            Task.FromResult(string.Empty);

        public Task AppendAsync(string sessionId, string markdown, CancellationToken cancellationToken = default)
        {
            if (ThrowOnAppend is not null)
            {
                throw ThrowOnAppend;
            }
            LastSessionId = sessionId;
            LastMarkdown = markdown;
            return Task.CompletedTask;
        }
    }

    private sealed class FixedClock : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public FixedClock(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
