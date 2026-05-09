using System;
using FluentAssertions;
using Xunit;

namespace CopilotSessionManager.Core.Tests.GitHub.Issues;

using CopilotSessionManager.Core.GitHub.Issues;

public class IssueRefTests
{
    [Fact]
    public void Constructor_lowercases_owner_repo_and_validates_number()
    {
        var r = new IssueRef("Owner/Repo", 5);
        r.OwnerRepo.Should().Be("owner/repo");
        r.Number.Should().Be(5);
    }

    [Fact]
    public void Constructor_throws_on_blank_repo_or_invalid_number()
    {
        Action a = () => new IssueRef("  ", 1);
        a.Should().Throw<ArgumentException>();

        Action b = () => new IssueRef("o/r", 0);
        b.Should().Throw<ArgumentOutOfRangeException>();

        Action c = () => new IssueRef("o/r", -7);
        c.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Equality_is_structural_and_case_insensitive_on_owner_repo()
    {
        new IssueRef("Org/Name", 1).Should().Be(new IssueRef("org/name", 1));
        new IssueRef("Org/Name", 1).Should().NotBe(new IssueRef("org/name", 2));
        new IssueRef("Org/Name", 1).Should().NotBe(new IssueRef("other/name", 1));
    }
}
