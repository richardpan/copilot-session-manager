using System;
using System.Collections.Generic;
using CopilotSessionManager.Core.Models;
using CopilotSessionManager.ViewModels;
using FluentAssertions;
using Xunit;

namespace CopilotSessionManager.Tests.Accessibility;

/// <summary>
/// A11y audit (#45): the dashboard cards expose an <c>AutomationName</c>
/// summary string that Narrator can read when focus lands on a card, plus
/// a <c>StatusGlyph</c> that pairs the colour-coded status pill with a
/// non-colour signal.
/// </summary>
public class SessionCardViewModelAutomationNameTests
{
    private static readonly DateTimeOffset Now = new(2026, 5, 8, 12, 0, 0, TimeSpan.Zero);

    private static Session BuildSession(
        SessionStatus status = SessionStatus.Idle,
        string? summary = "Refactor auth module",
        string? repo = "octo/widgets",
        string? branch = "main",
        DateTimeOffset? updatedAt = null) =>
        new(
            Id: "abcdef1234567890",
            Cwd: @"C:\ws\repo",
            Repository: repo,
            Branch: branch,
            Summary: summary,
            HostType: "cli",
            CreatedAt: Now.AddMinutes(-30),
            UpdatedAt: updatedAt ?? Now.AddMinutes(-2),
            TurnCount: 3,
            Status: status,
            CopilotVersion: CopilotVersion.Zero,
            Locks: Array.Empty<SessionLockInfo>());

    private static SessionCardViewModel BuildCard(
        SessionStatus status = SessionStatus.Idle,
        SessionType label = SessionType.Feature,
        string? summary = "Refactor auth module",
        string? repo = "octo/widgets",
        string? branch = "main") =>
        new(BuildSession(status, summary, repo, branch), label, new FixedClock(Now));

    [Theory]
    [InlineData(SessionStatus.Working, "▶")]
    [InlineData(SessionStatus.AwaitingApproval, "⚠")]
    [InlineData(SessionStatus.AwaitingInput, "✎")]
    [InlineData(SessionStatus.Idle, "◌")]
    [InlineData(SessionStatus.Inactive, "·")]
    [InlineData(SessionStatus.Orphaned, "✗")]
    public void StatusGlyph_PairsStateWithNonColourSignal(SessionStatus status, string expected)
    {
        BuildCard(status: status).StatusGlyph.Should().Be(expected);
    }

    [Fact]
    public void StatusGlyph_DistinctAcrossKnownStates()
    {
        var glyphs = new HashSet<string>();
        foreach (var status in new[]
        {
            SessionStatus.Working,
            SessionStatus.AwaitingApproval,
            SessionStatus.AwaitingInput,
            SessionStatus.Idle,
            SessionStatus.Inactive,
            SessionStatus.Orphaned,
        })
        {
            glyphs.Add(BuildCard(status: status).StatusGlyph).Should().BeTrue(
                $"each status should have a unique glyph (collision on {status})");
        }
    }

    [Fact]
    public void StatusBadgeText_CombinesGlyphAndLabel()
    {
        BuildCard(status: SessionStatus.Working).StatusBadgeText.Should().Be("▶ Working");
        BuildCard(status: SessionStatus.Orphaned).StatusBadgeText.Should().Be("✗ Crashed");
    }

    [Fact]
    public void AutomationName_IncludesLabel_Title_Status_RepoBranch_Updated()
    {
        var sut = BuildCard(
            status: SessionStatus.Working,
            label: SessionType.Bug,
            summary: "Fix flaky merge test",
            repo: "octo/widgets",
            branch: "fix/merge-flake");

        var name = sut.AutomationName;

        name.Should().Contain("Bug");
        name.Should().Contain("Fix flaky merge test");
        name.Should().Contain("Working");
        name.Should().Contain("octo/widgets");
        name.Should().Contain("fix/merge-flake");
        name.Should().Contain("min ago");
    }

    [Fact]
    public void AutomationName_FallsBackForMissingRepoAndBranch()
    {
        var sut = BuildCard(repo: null, branch: null, summary: "Quick poke");

        sut.AutomationName.Should().Contain("no repo");
        sut.AutomationName.Should().Contain("no branch");
    }

    [Fact]
    public void UpdateLabel_RaisesAutomationNameChange()
    {
        var sut = BuildCard(label: SessionType.Feature);
        var raised = new List<string?>();
        sut.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        sut.UpdateLabel(SessionType.Bug);

        raised.Should().Contain(nameof(SessionCardViewModel.AutomationName));
    }

    private sealed class FixedClock : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public FixedClock(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
