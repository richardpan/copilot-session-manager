using CopilotSessionManager.Core.GitHub;
using CopilotSessionManager.Core.Models;
using FluentAssertions;
using Xunit;

namespace CopilotSessionManager.Core.Tests.GitHub;

public class GitHubLinkResolverTests
{
    private readonly GitHubLinkResolver _sut = new();

    private static Session Make(string? repository, string? branch) => new(
        Id: "abc",
        Cwd: null,
        Repository: repository,
        Branch: branch,
        Summary: null,
        HostType: null,
        CreatedAt: DateTimeOffset.MinValue,
        UpdatedAt: DateTimeOffset.MinValue,
        TurnCount: 0,
        Status: SessionStatus.Idle,
        CopilotVersion: CopilotVersion.Zero,
        Locks: Array.Empty<SessionLockInfo>());

    [Fact]
    public void NullRepository_ReturnsEmpty()
    {
        var links = _sut.Resolve(Make(null, "main"));
        links.Should().BeSameAs(SessionGitHubLinks.Empty);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("local-only")]
    [InlineData("/just/a/path")]
    [InlineData("owner/")]
    [InlineData("/name")]
    public void NonGitHubLikeSlug_ReturnsEmpty(string repo)
    {
        _sut.Resolve(Make(repo, "main")).Should().BeSameAs(SessionGitHubLinks.Empty);
    }

    [Theory]
    [InlineData("richardpan/copilot-session-manager")]
    [InlineData("OWNER/Repo.Name")]
    [InlineData("a/b")]
    public void SimpleSlug_BuildsRepoUrl(string repo)
    {
        var links = _sut.Resolve(Make(repo, branch: null));
        links.RepositoryUrl.Should().Be($"https://github.com/{repo}");
        links.BranchUrl.Should().BeNull();
        links.PullRequest.Should().BeNull();
    }

    [Theory]
    [InlineData("https://github.com/richardpan/copilot-session-manager", "richardpan/copilot-session-manager")]
    [InlineData("https://github.com/richardpan/copilot-session-manager.git", "richardpan/copilot-session-manager")]
    [InlineData("git@github.com:richardpan/copilot-session-manager.git", "richardpan/copilot-session-manager")]
    [InlineData("http://github.com/richardpan/copilot-session-manager/", "richardpan/copilot-session-manager")]
    public void NormalizesUrlsAndSshToCanonicalSlug(string raw, string expectedSlug)
    {
        var links = _sut.Resolve(Make(raw, "main"));
        links.RepositoryUrl.Should().Be($"https://github.com/{expectedSlug}");
        links.BranchUrl.Should().Be($"https://github.com/{expectedSlug}/tree/main");
    }

    [Fact]
    public void BranchWithSpecialChars_IsUrlEncoded()
    {
        var links = _sut.Resolve(Make("a/b", "feat/branch with spaces"));
        links.BranchUrl.Should().Be("https://github.com/a/b/tree/feat%2Fbranch%20with%20spaces");
    }

    [Fact]
    public void EmptyBranch_ProducesRepoUrlOnly()
    {
        var links = _sut.Resolve(Make("a/b", "   "));
        links.RepositoryUrl.Should().Be("https://github.com/a/b");
        links.BranchUrl.Should().BeNull();
    }

    [Fact]
    public void HasAnyLink_ReflectsContents()
    {
        SessionGitHubLinks.Empty.HasAnyLink.Should().BeFalse();
        _sut.Resolve(Make("a/b", null)).HasAnyLink.Should().BeTrue();
    }
}
