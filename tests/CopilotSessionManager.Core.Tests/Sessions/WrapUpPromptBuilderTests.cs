using System;
using CopilotSessionManager.Core.Models;
using CopilotSessionManager.Core.Sessions;
using FluentAssertions;
using Xunit;

namespace CopilotSessionManager.Core.Tests.Sessions;

/// <summary>
/// Tests for the V1.3 (#149) wrap-up prompt builder.
/// </summary>
public class WrapUpPromptBuilderTests
{
    private static Session BuildSession(
        string id = "sess-1",
        string? summary = "Refactor the build pipeline",
        string? repository = "owner/repo",
        string? branch = "feat/build") =>
        new(
            Id: id,
            Cwd: null,
            Repository: repository,
            Branch: branch,
            Summary: summary,
            HostType: "cli",
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow,
            TurnCount: 1,
            Status: SessionStatus.Idle,
            CopilotVersion: CopilotVersion.Zero,
            Locks: Array.Empty<SessionLockInfo>());

    [Fact]
    public void Build_SubstitutesAllFourTokens()
    {
        var template = "id={sessionId} summary={summary} repo={repository} branch={branch}";
        var result = WrapUpPromptBuilder.Build(template, BuildSession());

        result.Should().Be("id=sess-1 summary=Refactor the build pipeline repo=owner/repo branch=feat/build");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Build_RendersUnknownForNullOrWhitespaceFields(string? value)
    {
        var template = "summary={summary} repo={repository} branch={branch}";
        var session = BuildSession(summary: value, repository: value, branch: value);

        var result = WrapUpPromptBuilder.Build(template, session);

        result.Should().Be("summary=(unknown) repo=(unknown) branch=(unknown)");
    }

    [Fact]
    public void Build_LeavesUnknownPlaceholdersLiteral()
    {
        var template = "{sessionId} {nonsense} {alsoMissing}";
        var result = WrapUpPromptBuilder.Build(template, BuildSession());

        result.Should().Be("sess-1 {nonsense} {alsoMissing}");
    }

    [Fact]
    public void Build_IsCaseInsensitiveOnTokenNames()
    {
        var template = "{SessionId} {SUMMARY} {Repository}";
        var result = WrapUpPromptBuilder.Build(template, BuildSession());

        result.Should().Be("sess-1 Refactor the build pipeline owner/repo");
    }

    [Fact]
    public void Build_ThrowsOnNullArguments()
    {
        Assert.Throws<ArgumentNullException>(() => WrapUpPromptBuilder.Build(null!, BuildSession()));
        Assert.Throws<ArgumentNullException>(() => WrapUpPromptBuilder.Build("template", null!));
    }
}
