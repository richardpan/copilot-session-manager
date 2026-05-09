using System;
using System.Linq;
using CopilotSessionManager.Core.GitHub.Checks;
using FluentAssertions;
using Xunit;

namespace CopilotSessionManager.Core.Tests.GitHub.Checks;

/// <summary>
/// Unit tests for the <see cref="GhChecksJsonParser"/> JSON-rollup logic.
/// Drives only the parser so process plumbing stays out of the picture.
/// </summary>
public class GhChecksJsonParserTests
{
    [Fact]
    public void Empty_array_returns_None_rollup()
    {
        var summary = GhChecksJsonParser.Parse("[]");
        summary.Should().NotBeNull();
        summary!.Rollup.Should().Be(PullRequestCheckRollup.None);
        summary.AttentionCheckNames.Should().BeEmpty();
    }

    [Fact]
    public void All_pass_returns_Success()
    {
        const string json = """
        [
          {"name":"build","state":"SUCCESS","bucket":"pass"},
          {"name":"test","state":"SUCCESS","bucket":"pass"}
        ]
        """;
        var summary = GhChecksJsonParser.Parse(json);
        summary.Should().NotBeNull();
        summary!.Rollup.Should().Be(PullRequestCheckRollup.Success);
        summary.AttentionCheckNames.Should().BeEmpty();
    }

    [Fact]
    public void Any_fail_returns_Failure_with_failing_names()
    {
        const string json = """
        [
          {"name":"build","state":"SUCCESS","bucket":"pass"},
          {"name":"lint","state":"FAILURE","bucket":"fail"}
        ]
        """;
        var summary = GhChecksJsonParser.Parse(json);
        summary.Should().NotBeNull();
        summary!.Rollup.Should().Be(PullRequestCheckRollup.Failure);
        summary.AttentionCheckNames.Should().ContainSingle().Which.Should().Be("lint");
    }

    [Fact]
    public void Any_pending_returns_Pending_with_pending_names()
    {
        const string json = """
        [
          {"name":"build","state":"SUCCESS","bucket":"pass"},
          {"name":"e2e","state":"IN_PROGRESS","bucket":"pending"}
        ]
        """;
        var summary = GhChecksJsonParser.Parse(json);
        summary.Should().NotBeNull();
        summary!.Rollup.Should().Be(PullRequestCheckRollup.Pending);
        summary.AttentionCheckNames.Should().ContainSingle().Which.Should().Be("e2e");
    }

    [Fact]
    public void Failure_dominates_pending_in_rollup()
    {
        const string json = """
        [
          {"name":"build","state":"SUCCESS","bucket":"pass"},
          {"name":"lint","state":"FAILURE","bucket":"fail"},
          {"name":"e2e","state":"IN_PROGRESS","bucket":"pending"}
        ]
        """;
        var summary = GhChecksJsonParser.Parse(json);
        summary!.Rollup.Should().Be(PullRequestCheckRollup.Failure);
        summary.AttentionCheckNames.Should().Contain(new[] { "lint", "e2e" });
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("CANCELLED")]
    [InlineData("action_required")]
    [InlineData("timeout")]
    [InlineData("error")]
    [InlineData("startup_failure")]
    public void Failure_buckets_are_classified_as_Failure(string bucket)
    {
        var json = "[{\"name\":\"x\",\"state\":\"COMPLETED\",\"bucket\":\"" + bucket + "\"}]";
        GhChecksJsonParser.Parse(json)!.Rollup.Should().Be(PullRequestCheckRollup.Failure);
    }

    [Theory]
    [InlineData("queued")]
    [InlineData("in_progress")]
    [InlineData("waiting")]
    [InlineData("requested")]
    public void Pending_buckets_are_classified_as_Pending(string bucket)
    {
        var json = "[{\"name\":\"x\",\"state\":\"COMPLETED\",\"bucket\":\"" + bucket + "\"}]";
        GhChecksJsonParser.Parse(json)!.Rollup.Should().Be(PullRequestCheckRollup.Pending);
    }

    [Theory]
    [InlineData("neutral")]
    [InlineData("skipping")]
    [InlineData("skipped")]
    [InlineData("pass")]
    public void Neutral_or_skipped_are_classified_as_Success(string bucket)
    {
        var json = "[{\"name\":\"x\",\"state\":\"COMPLETED\",\"bucket\":\"" + bucket + "\"}]";
        GhChecksJsonParser.Parse(json)!.Rollup.Should().Be(PullRequestCheckRollup.Success);
    }

    [Fact]
    public void Falls_back_to_state_when_bucket_missing()
    {
        // Older `gh` versions don't always emit `bucket`; classifier should
        // tolerate that and use `state`.
        const string json = """
        [{"name":"x","state":"FAILURE"}]
        """;
        GhChecksJsonParser.Parse(json)!.Rollup.Should().Be(PullRequestCheckRollup.Failure);
    }

    [Fact]
    public void Unknown_bucket_does_not_dominate_other_signals()
    {
        const string json = """
        [
          {"name":"weird","state":"COMPLETED","bucket":"mystery"},
          {"name":"build","state":"SUCCESS","bucket":"pass"}
        ]
        """;
        GhChecksJsonParser.Parse(json)!.Rollup.Should().Be(PullRequestCheckRollup.Success);
    }

    [Fact]
    public void Malformed_json_returns_null()
    {
        GhChecksJsonParser.Parse("not json").Should().BeNull();
        GhChecksJsonParser.Parse("{not an array}").Should().BeNull();
    }

    [Fact]
    public void Whitespace_or_empty_returns_null()
    {
        GhChecksJsonParser.Parse("").Should().BeNull();
        GhChecksJsonParser.Parse("   ").Should().BeNull();
    }

    [Fact]
    public void Object_payload_returns_null_not_throws()
    {
        // gh emits arrays, but a future format change shouldn't crash.
        GhChecksJsonParser.Parse("{\"foo\":1}").Should().BeNull();
    }

    [Fact]
    public void Success_rollup_clears_attention_names_even_if_payload_includes_them()
    {
        // Defensive: every "attention" entry only flips into the list when
        // its individual bucket is failure/pending. Sanity-check Success
        // never has names.
        const string json = """
        [
          {"name":"build","state":"SUCCESS","bucket":"pass"},
          {"name":"docs","state":"SKIPPED","bucket":"skipping"}
        ]
        """;
        var s = GhChecksJsonParser.Parse(json)!;
        s.Rollup.Should().Be(PullRequestCheckRollup.Success);
        s.AttentionCheckNames.Should().BeEmpty();
    }
}
