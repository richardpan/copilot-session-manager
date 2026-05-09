using CopilotSessionManager.Core.GitHub;
using CopilotSessionManager.Core.Models;
using FluentAssertions;
using Xunit;

namespace CopilotSessionManager.Core.Tests.GitHub;

/// <summary>
/// The link resolver is intentionally a pure, I/O-free builder of repo and
/// branch URLs from a session record (PR enrichment is a separate
/// out-of-band fetch via <see cref="IGitHubClient"/>). These tests pin down
/// the offline contract: even when GitHub is offline, the resolver still
/// returns the static repo + branch links without throwing — the only
/// degradation is that <see cref="SessionGitHubLinks.PullRequest"/> stays
/// <c>null</c> until network recovers.
/// </summary>
public class GitHubLinkResolverOfflineTests
{
    private static Session Make(string? repo, string? branch) => new(
        Id: "abc",
        Cwd: null,
        Repository: repo,
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
    public void Resolver_StillProducesRepoAndBranchUrls_WhenGitHubIsOffline()
    {
        // Resolver is pure — it has no GitHub dependency at all. This test
        // documents that contract: in Offline mode, the UI still gets
        // click-throughs for repo + branch (just no PR badge).
        var resolver = new GitHubLinkResolver();

        var links = resolver.Resolve(Make("owner/repo", "feat/something"));

        links.RepositoryUrl.Should().Be("https://github.com/owner/repo");
        links.BranchUrl.Should().Be("https://github.com/owner/repo/tree/feat%2Fsomething");
        links.PullRequest.Should().BeNull(); // the degraded part — filled in later by IGitHubClient
        links.HasAnyLink.Should().BeTrue();
    }

    [Fact]
    public void Resolver_DoesNotThrow_OnNullSession()
    {
        var resolver = new GitHubLinkResolver();
        var act = () => resolver.Resolve(null!);
        act.Should().Throw<ArgumentNullException>();
    }
}
