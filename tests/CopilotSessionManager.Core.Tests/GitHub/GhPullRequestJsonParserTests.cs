using CopilotSessionManager.Core.GitHub;
using CopilotSessionManager.Core.Models;
using FluentAssertions;
using Xunit;

namespace CopilotSessionManager.Core.Tests.GitHub;

public class GhPullRequestJsonParserTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-json")]
    [InlineData("{}")]
    [InlineData("[]")]
    public void EmptyOrInvalid_ReturnsNull(string json)
    {
        GhPullRequestJsonParser.ParseFirst(json).Should().BeNull();
    }

    [Fact]
    public void OpenPullRequest_IsMapped()
    {
        const string json = """
        [{"number":42,"title":"Add thing","state":"OPEN","isDraft":false,"url":"https://github.com/o/r/pull/42"}]
        """;
        var pr = GhPullRequestJsonParser.ParseFirst(json)!;
        pr.Number.Should().Be(42);
        pr.Title.Should().Be("Add thing");
        pr.State.Should().Be(PullRequestState.Open);
        pr.Url.Should().Be("https://github.com/o/r/pull/42");
    }

    [Fact]
    public void OpenPullRequest_WithDraftFlag_IsMappedToDraft()
    {
        const string json = """
        [{"number":7,"title":"WIP","state":"OPEN","isDraft":true,"url":"https://github.com/o/r/pull/7"}]
        """;
        GhPullRequestJsonParser.ParseFirst(json)!.State.Should().Be(PullRequestState.Draft);
    }

    [Fact]
    public void MergedPullRequest_IsMapped()
    {
        const string json = """
        [{"number":99,"title":"Done","state":"MERGED","isDraft":false,"url":"https://github.com/o/r/pull/99"}]
        """;
        GhPullRequestJsonParser.ParseFirst(json)!.State.Should().Be(PullRequestState.Merged);
    }

    [Fact]
    public void ClosedPullRequest_IsMapped()
    {
        const string json = """
        [{"number":3,"title":"Nope","state":"CLOSED","isDraft":false,"url":"https://github.com/o/r/pull/3"}]
        """;
        GhPullRequestJsonParser.ParseFirst(json)!.State.Should().Be(PullRequestState.Closed);
    }

    [Fact]
    public void FirstEntry_IsReturnedWhenMultiple()
    {
        const string json = """
        [
          {"number":10,"title":"A","state":"OPEN","isDraft":false,"url":"u1"},
          {"number":11,"title":"B","state":"OPEN","isDraft":false,"url":"u2"}
        ]
        """;
        GhPullRequestJsonParser.ParseFirst(json)!.Number.Should().Be(10);
    }

    [Fact]
    public void MissingNumber_ReturnsNull()
    {
        const string json = """
        [{"title":"no number","state":"OPEN","isDraft":false,"url":"u"}]
        """;
        GhPullRequestJsonParser.ParseFirst(json).Should().BeNull();
    }

    [Fact]
    public void UnknownState_MapsToUnknown()
    {
        const string json = """
        [{"number":1,"title":"x","state":"WAT","isDraft":false,"url":"u"}]
        """;
        GhPullRequestJsonParser.ParseFirst(json)!.State.Should().Be(PullRequestState.Unknown);
    }
}
