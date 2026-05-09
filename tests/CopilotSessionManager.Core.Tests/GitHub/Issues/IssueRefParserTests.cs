using CopilotSessionManager.Core.GitHub.Issues;
using FluentAssertions;
using Xunit;

namespace CopilotSessionManager.Core.Tests.GitHub.Issues;

/// <summary>
/// Exhaustive coverage for <see cref="IssueRefParser.TryParse"/>: every
/// shape it accepts (including the URL form), the default-owner/repo
/// fallback, and rejection of malformed / out-of-bounds / pull-request
/// inputs.
/// </summary>
public class IssueRefParserTests
{
    [Theory]
    [InlineData("owner/repo#42", "owner/repo", 42)]
    [InlineData("Owner/Repo#7", "owner/repo", 7)]
    [InlineData("ORG/My-Repo.NET#1", "org/my-repo.net", 1)]
    [InlineData("a/b#9999", "a/b", 9999)]
    public void Accepts_owner_repo_hash_form(string input, string expectedOwnerRepo, int expectedNumber)
    {
        IssueRefParser.TryParse(input, defaultOwnerRepo: null, out var issueRef).Should().BeTrue();
        issueRef.Should().NotBeNull();
        issueRef!.OwnerRepo.Should().Be(expectedOwnerRepo);
        issueRef.Number.Should().Be(expectedNumber);
    }

    [Theory]
    [InlineData("#42", "richardpan/csm", 42)]
    [InlineData("42", "richardpan/csm", 42)]
    [InlineData("  #42  ", "richardpan/csm", 42)]
    [InlineData("  42  ", "richardpan/csm", 42)]
    public void Accepts_bare_number_with_default_repo(string input, string defaultRepo, int expectedNumber)
    {
        IssueRefParser.TryParse(input, defaultRepo, out var issueRef).Should().BeTrue();
        issueRef!.OwnerRepo.Should().Be(defaultRepo.ToLowerInvariant());
        issueRef.Number.Should().Be(expectedNumber);
    }

    [Theory]
    [InlineData("https://github.com/owner/repo/issues/42", "owner/repo", 42)]
    [InlineData("http://github.com/owner/Repo/issues/7", "owner/repo", 7)]
    [InlineData("https://www.github.com/Org/My-Repo/issues/123", "org/my-repo", 123)]
    [InlineData("https://github.com/owner/repo/issues/9?from=foo", "owner/repo", 9)]
    [InlineData("https://github.com/owner/repo/issues/9#comment", "owner/repo", 9)]
    public void Accepts_full_issue_url(string url, string expectedOwnerRepo, int expectedNumber)
    {
        IssueRefParser.TryParse(url, defaultOwnerRepo: null, out var issueRef).Should().BeTrue();
        issueRef!.OwnerRepo.Should().Be(expectedOwnerRepo);
        issueRef.Number.Should().Be(expectedNumber);
    }

    [Theory]
    [InlineData("https://github.com/owner/repo/pull/42")]
    [InlineData("https://github.com/owner/repo/pull/1")]
    [InlineData("HTTP://GITHUB.COM/owner/repo/pull/9")]
    public void Rejects_pull_request_urls(string url)
    {
        IssueRefParser.TryParse(url, defaultOwnerRepo: "owner/repo", out var issueRef).Should().BeFalse();
        issueRef.Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-ref")]
    [InlineData("owner#42")]
    [InlineData("/repo#42")]
    [InlineData("owner/#42")]
    [InlineData("owner/repo#0")]
    [InlineData("owner/repo#-3")]
    [InlineData("owner/repo#abc")]
    [InlineData("owner/repo#")]
    [InlineData("-bad/repo#1")]
    [InlineData("owner/repo!#1")]
    public void Rejects_malformed_input(string input)
    {
        IssueRefParser.TryParse(input, defaultOwnerRepo: null, out var issueRef).Should().BeFalse();
        issueRef.Should().BeNull();
    }

    [Fact]
    public void Bare_number_without_default_owner_repo_returns_false()
    {
        IssueRefParser.TryParse("#5", defaultOwnerRepo: null, out var issueRef).Should().BeFalse();
        issueRef.Should().BeNull();
        IssueRefParser.TryParse("5", defaultOwnerRepo: "   ", out var issueRef2).Should().BeFalse();
        issueRef2.Should().BeNull();
    }

    [Fact]
    public void Bare_number_with_invalid_default_owner_repo_returns_false()
    {
        IssueRefParser.TryParse("#5", defaultOwnerRepo: "not a slug", out var issueRef).Should().BeFalse();
        issueRef.Should().BeNull();
    }

    [Fact]
    public void Owner_repo_segments_too_long_are_rejected()
    {
        var longOwner = new string('a', 40);
        IssueRefParser.TryParse($"{longOwner}/repo#1", defaultOwnerRepo: null, out var issueRef).Should().BeFalse();
        issueRef.Should().BeNull();
    }

    [Fact]
    public void Cross_repo_form_does_not_use_default_owner_repo()
    {
        IssueRefParser.TryParse("other/repo#9", defaultOwnerRepo: "main/repo", out var issueRef).Should().BeTrue();
        issueRef!.OwnerRepo.Should().Be("other/repo");
    }

    [Fact]
    public void Returns_canonical_string_and_url()
    {
        IssueRefParser.TryParse("Org/Name#42", defaultOwnerRepo: null, out var issueRef).Should().BeTrue();
        issueRef!.ToString().Should().Be("org/name#42");
        issueRef.ToCanonicalUrl().Should().Be("https://github.com/org/name/issues/42");
    }
}
