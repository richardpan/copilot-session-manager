using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CopilotSessionManager.Core.Models;
using CopilotSessionManager.Core.Sessions;
using FluentAssertions;
using Moq;
using Xunit;

namespace CopilotSessionManager.Core.Tests.Sessions;

/// <summary>
/// V1.3 (#147) tests for <see cref="DocFreshnessService"/>: covers all
/// five <see cref="DocFreshnessState"/> transitions with synthetic file
/// timestamps and a fake clock.
/// </summary>
public sealed class DocFreshnessServiceTests : IDisposable
{
    /// <summary>Minimal fixed-time <see cref="TimeProvider"/> for deterministic tests.</summary>
    private sealed class FixedTimeProvider : TimeProvider
    {
        private DateTimeOffset _now;
        public FixedTimeProvider(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
    }

    private readonly string _root;
    private readonly Mock<ISessionFolderReader> _folders = new();
    private readonly FixedTimeProvider _clock = new(new DateTimeOffset(2025, 1, 15, 12, 0, 0, TimeSpan.Zero));

    public DocFreshnessServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "csm-docfresh-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        { Directory.Delete(_root, recursive: true); }
        catch { /* best effort */ }
    }

    private DocFreshnessService CreateSut() => new(_folders.Object, _clock);

    private string CreateSessionFolder(string sessionId)
    {
        var path = Path.Combine(_root, sessionId);
        Directory.CreateDirectory(path);
        _folders.Setup(f => f.GetSessionFolderPath(sessionId)).Returns(path);
        return path;
    }

    private void WriteReadme(string folder, DateTimeOffset mtime)
    {
        var path = Path.Combine(folder, "SESSION-README.md");
        File.WriteAllText(path, "stub");
        File.SetLastWriteTimeUtc(path, mtime.UtcDateTime);
    }

    [Fact]
    public void Evaluate_SessionUnderThirtyMinutes_IsNotApplicable()
    {
        CreateSessionFolder("s1");
        var createdAt = _clock.GetUtcNow().AddMinutes(-10);

        var result = CreateSut().Evaluate("s1", createdAt);

        result.State.Should().Be(DocFreshnessState.NotApplicable);
        result.AgeDays.Should().BeNull();
    }

    [Fact]
    public void Evaluate_NoReadmeOrDocs_IsMissing()
    {
        CreateSessionFolder("s2");
        var createdAt = _clock.GetUtcNow().AddHours(-2);

        var result = CreateSut().Evaluate("s2", createdAt);

        result.State.Should().Be(DocFreshnessState.Missing);
        result.AgeDays.Should().BeNull();
    }

    [Fact]
    public void Evaluate_ReadmeWrittenWithinOneDay_IsFresh()
    {
        var folder = CreateSessionFolder("s3");
        var createdAt = _clock.GetUtcNow().AddDays(-2);
        WriteReadme(folder, _clock.GetUtcNow().AddHours(-3));

        var result = CreateSut().Evaluate("s3", createdAt);

        result.State.Should().Be(DocFreshnessState.Fresh);
        result.AgeDays.Should().BeNull();
    }

    [Fact]
    public void Evaluate_ReadmeBetweenOneAndSevenDays_IsStaleWithAgeDays()
    {
        var folder = CreateSessionFolder("s4");
        var createdAt = _clock.GetUtcNow().AddDays(-10);
        WriteReadme(folder, _clock.GetUtcNow().AddDays(-4));

        var result = CreateSut().Evaluate("s4", createdAt);

        result.State.Should().Be(DocFreshnessState.Stale);
        result.AgeDays.Should().Be(4);
    }

    [Fact]
    public void Evaluate_ReadmeOlderThanSevenDays_IsVeryStaleWithAgeDays()
    {
        var folder = CreateSessionFolder("s5");
        var createdAt = _clock.GetUtcNow().AddDays(-30);
        WriteReadme(folder, _clock.GetUtcNow().AddDays(-12));

        var result = CreateSut().Evaluate("s5", createdAt);

        result.State.Should().Be(DocFreshnessState.VeryStale);
        result.AgeDays.Should().Be(12);
    }

    [Fact]
    public void Evaluate_PrefersNewerOfReadmeAndDocs()
    {
        var folder = CreateSessionFolder("s6");
        var createdAt = _clock.GetUtcNow().AddDays(-30);
        WriteReadme(folder, _clock.GetUtcNow().AddDays(-20));
        var docs = Path.Combine(folder, "SESSION-DOCS.md");
        File.WriteAllText(docs, "fresh docs");
        File.SetLastWriteTimeUtc(docs, _clock.GetUtcNow().AddHours(-5).UtcDateTime);

        var result = CreateSut().Evaluate("s6", createdAt);

        result.State.Should().Be(DocFreshnessState.Fresh);
    }

    [Fact]
    public void Evaluate_AtExactlyOneDay_StaysFresh()
    {
        var folder = CreateSessionFolder("s7");
        var createdAt = _clock.GetUtcNow().AddDays(-2);
        WriteReadme(folder, _clock.GetUtcNow().AddDays(-1));

        var result = CreateSut().Evaluate("s7", createdAt);

        result.State.Should().Be(DocFreshnessState.Fresh);
    }

    [Fact]
    public void Evaluate_NullOrWhitespaceSessionId_Throws()
    {
        var sut = CreateSut();
        var now = _clock.GetUtcNow();

        Action act = () => sut.Evaluate(string.Empty, now);

        act.Should().Throw<ArgumentException>();
    }
}
