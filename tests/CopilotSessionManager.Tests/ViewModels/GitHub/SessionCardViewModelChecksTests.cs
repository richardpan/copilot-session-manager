using System;
using System.Collections.Generic;
using CopilotSessionManager.Core.GitHub;
using CopilotSessionManager.Core.GitHub.Checks;
using CopilotSessionManager.Core.Models;
using CopilotSessionManager.Services;
using CopilotSessionManager.ViewModels;
using FluentAssertions;
using Xunit;

namespace CopilotSessionManager.Tests.ViewModels.GitHub;

/// <summary>
/// Behaviour around the inline CI rollup badge maintained by
/// <see cref="SessionCardViewModel"/> alongside the existing PR badge.
/// </summary>
public class SessionCardViewModelChecksTests
{
    private static readonly DateTimeOffset Now = new(2026, 5, 8, 12, 0, 0, TimeSpan.Zero);

    private static Session BuildSession(SessionGitHubLinks? links = null) => new(
        Id: "abc",
        Cwd: null,
        Repository: "owner/repo",
        Branch: "main",
        Summary: "x",
        HostType: "cli",
        CreatedAt: Now.AddMinutes(-10),
        UpdatedAt: Now.AddMinutes(-1),
        TurnCount: 1,
        Status: SessionStatus.Idle,
        CopilotVersion: CopilotVersion.Zero,
        Locks: Array.Empty<SessionLockInfo>(),
        ModelInfo: null,
        GitHubLinks: links);

    private static SessionCardViewModel BuildCard(SessionGitHubLinks? links = null)
    {
        var tp = new FixedTimeProvider(Now);
        return new SessionCardViewModel(BuildSession(links), SessionType.Exploratory, tp,
            modelCatalog: null, costCalculator: null, fileLauncher: null);
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public FixedTimeProvider(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
    }

    private static PullRequestInfo SamplePr() =>
        new(42, "feat", PullRequestState.Open, "https://github.com/o/r/pull/42");

    [Fact]
    public void Initially_HasChecks_is_false_and_rollup_is_None()
    {
        var card = BuildCard();
        card.CheckRollup.Should().Be(PullRequestCheckRollup.None);
        card.HasChecks.Should().BeFalse();
        card.CheckBadgeText.Should().BeEmpty();
    }

    [Fact]
    public void SetChecks_without_PR_keeps_HasChecks_false()
    {
        // The badge only renders alongside a PR — without one, it stays
        // hidden even if a rollup is somehow set.
        var card = BuildCard();
        card.SetChecks(new PullRequestCheckSummary(PullRequestCheckRollup.Success, Array.Empty<string>()));
        card.HasChecks.Should().BeFalse();
    }

    [Theory]
    [InlineData(PullRequestCheckRollup.Success, "\u2713", "All checks passing")]
    [InlineData(PullRequestCheckRollup.Failure, "\u2717", "Checks failing")]
    [InlineData(PullRequestCheckRollup.Pending, "\u25CF", "Checks running")]
    public void Each_rollup_maps_to_glyph_and_tooltip(
        PullRequestCheckRollup rollup, string expectedGlyph, string expectedTooltipPrefix)
    {
        var card = BuildCard();
        card.SetPullRequest(SamplePr());
        card.SetChecks(new PullRequestCheckSummary(rollup, Array.Empty<string>()));

        card.CheckRollup.Should().Be(rollup);
        card.HasChecks.Should().BeTrue();
        card.CheckBadgeText.Should().Be(expectedGlyph);
        card.CheckTooltip.Should().StartWith(expectedTooltipPrefix);
        card.CheckBadgeBrush.Should().NotBeNull();
    }

    [Fact]
    public void Failure_tooltip_includes_attention_check_names()
    {
        var card = BuildCard();
        card.SetPullRequest(SamplePr());
        card.SetChecks(new PullRequestCheckSummary(
            PullRequestCheckRollup.Failure,
            new[] { "lint", "build" }));

        card.CheckTooltip.Should().Contain("lint").And.Contain("build");
    }

    [Fact]
    public void SetChecks_raises_property_changed_for_badge_surface()
    {
        var card = BuildCard();
        card.SetPullRequest(SamplePr());
        var raised = new List<string>();
        card.PropertyChanged += (_, e) => raised.Add(e.PropertyName!);

        card.SetChecks(new PullRequestCheckSummary(PullRequestCheckRollup.Success, Array.Empty<string>()));

        raised.Should().Contain(new[]
        {
            nameof(SessionCardViewModel.CheckRollup),
            nameof(SessionCardViewModel.HasChecks),
            nameof(SessionCardViewModel.CheckBadgeText),
            nameof(SessionCardViewModel.CheckBadgeBrush),
            nameof(SessionCardViewModel.CheckTooltip),
        });
    }

    [Fact]
    public void SetPullRequest_clears_a_previously_set_check_override()
    {
        // A new PR resolution invalidates any cached rollup — the next
        // discovery snapshot must re-fetch checks for the new head SHA.
        var card = BuildCard();
        card.SetPullRequest(SamplePr());
        card.SetChecks(new PullRequestCheckSummary(PullRequestCheckRollup.Failure, new[] { "lint" }));
        card.HasChecks.Should().BeTrue();

        card.SetPullRequest(new PullRequestInfo(43, "feat-2", PullRequestState.Open, "https://github.com/o/r/pull/43"));

        card.HasChecks.Should().BeFalse();
        card.CheckRollup.Should().Be(PullRequestCheckRollup.None);
    }

    [Fact]
    public void SetPullRequest_null_also_hides_checks()
    {
        var card = BuildCard();
        card.SetPullRequest(SamplePr());
        card.SetChecks(new PullRequestCheckSummary(PullRequestCheckRollup.Success, Array.Empty<string>()));

        card.SetPullRequest(null);

        card.HasChecks.Should().BeFalse();
    }

    [Fact]
    public void UpdateFrom_resets_check_override()
    {
        var card = BuildCard();
        card.SetPullRequest(SamplePr());
        card.SetChecks(new PullRequestCheckSummary(PullRequestCheckRollup.Failure, new[] { "lint" }));

        card.UpdateFrom(BuildSession());

        card.CheckRollup.Should().Be(PullRequestCheckRollup.None);
        card.HasChecks.Should().BeFalse();
    }

    [Fact]
    public void Setting_null_summary_clears_indicator()
    {
        var card = BuildCard();
        card.SetPullRequest(SamplePr());
        card.SetChecks(new PullRequestCheckSummary(PullRequestCheckRollup.Success, Array.Empty<string>()));

        card.SetChecks(null);

        card.HasChecks.Should().BeFalse();
        card.CheckRollup.Should().Be(PullRequestCheckRollup.None);
    }
}
