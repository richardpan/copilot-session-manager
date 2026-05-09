using System;
using System.Collections.Generic;
using CopilotSessionManager.Core.Models;
using CopilotSessionManager.ViewModels;
using CopilotSessionManager.ViewModels.Merge;
using FluentAssertions;
using Xunit;

namespace CopilotSessionManager.Tests.ViewModels.Merge;

public class MergeTargetCandidateViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 5, 9, 12, 0, 0, TimeSpan.Zero);

    private static Session BuildSession(
        string id = "deadbeefcafef00d",
        SessionStatus status = SessionStatus.Idle,
        DateTimeOffset? updatedAt = null,
        string? repository = "owner/repo",
        string? branch = "main") =>
        new(
            Id: id,
            Cwd: @"C:\ws\repo",
            Repository: repository,
            Branch: branch,
            Summary: "A session",
            HostType: "cli",
            CreatedAt: Now.AddMinutes(-30),
            UpdatedAt: updatedAt ?? Now.AddMinutes(-2),
            TurnCount: 1,
            Status: status,
            CopilotVersion: CopilotVersion.Zero,
            Locks: Array.Empty<SessionLockInfo>());

    private static SessionCardViewModel Card(
        string id = "deadbeefcafef00d",
        SessionStatus status = SessionStatus.Idle,
        DateTimeOffset? updatedAt = null,
        string? repository = "owner/repo",
        string? branch = "main") =>
        new(BuildSession(id, status, updatedAt, repository, branch),
            new SessionsViewModelTests.FixedTimeProvider(Now));

    [Fact]
    public void Constructor_NullCard_Throws()
    {
        var act = () => new MergeTargetCandidateViewModel(null!, new SessionsViewModelTests.FixedTimeProvider(Now));
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_NullClock_Throws()
    {
        var act = () => new MergeTargetCandidateViewModel(Card(), null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void IsActive_TrueForActiveStatuses()
    {
        var clock = new SessionsViewModelTests.FixedTimeProvider(Now);
        new MergeTargetCandidateViewModel(Card(status: SessionStatus.Working), clock).IsActive.Should().BeTrue();
        new MergeTargetCandidateViewModel(Card(status: SessionStatus.AwaitingApproval), clock).IsActive.Should().BeTrue();
        new MergeTargetCandidateViewModel(Card(status: SessionStatus.AwaitingInput), clock).IsActive.Should().BeTrue();
        new MergeTargetCandidateViewModel(Card(status: SessionStatus.Idle), clock).IsActive.Should().BeTrue();
    }

    [Fact]
    public void IsActive_FalseForInactiveOrCrashedStatuses()
    {
        var clock = new SessionsViewModelTests.FixedTimeProvider(Now);
        new MergeTargetCandidateViewModel(Card(status: SessionStatus.Inactive), clock).IsActive.Should().BeFalse();
        new MergeTargetCandidateViewModel(Card(status: SessionStatus.Orphaned), clock).IsActive.Should().BeFalse();
    }

    [Fact]
    public void Subtitle_PrefersRepoAndBranchThenFallbacks()
    {
        var clock = new SessionsViewModelTests.FixedTimeProvider(Now);
        new MergeTargetCandidateViewModel(Card(repository: "o/r", branch: "main"), clock)
            .Subtitle.Should().Be("o/r @ main");
        new MergeTargetCandidateViewModel(Card(repository: "o/r", branch: null), clock)
            .Subtitle.Should().Be("o/r");
        new MergeTargetCandidateViewModel(Card(repository: null, branch: "feat"), clock)
            .Subtitle.Should().Be("feat");
    }

    [Fact]
    public void RecencyDescription_FormatsRelativeTime()
    {
        var clock = new SessionsViewModelTests.FixedTimeProvider(Now);
        new MergeTargetCandidateViewModel(Card(updatedAt: Now.AddMinutes(-2), status: SessionStatus.Working), clock)
            .RecencyDescription.Should().Be("active 2 min ago");
        new MergeTargetCandidateViewModel(Card(updatedAt: Now.AddHours(-3), status: SessionStatus.Inactive), clock)
            .RecencyDescription.Should().Be("updated 3 hr ago");
        new MergeTargetCandidateViewModel(Card(updatedAt: Now.AddDays(-2), status: SessionStatus.Inactive), clock)
            .RecencyDescription.Should().Be("updated 2 d ago");
        new MergeTargetCandidateViewModel(Card(updatedAt: Now.AddSeconds(-5), status: SessionStatus.Idle), clock)
            .RecencyDescription.Should().Be("active just now");
    }

    [Fact]
    public void RecencyDescription_HandlesMissingUpdatedAt()
    {
        var clock = new SessionsViewModelTests.FixedTimeProvider(Now);
        new MergeTargetCandidateViewModel(Card(updatedAt: DateTimeOffset.MinValue), clock)
            .RecencyDescription.Should().Be("never updated");
    }

    [Fact]
    public void IsSelected_RaisesPropertyChanged()
    {
        var clock = new SessionsViewModelTests.FixedTimeProvider(Now);
        var sut = new MergeTargetCandidateViewModel(Card(), clock);
        var raised = new List<string?>();
        sut.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        sut.IsSelected = true;

        raised.Should().Contain(nameof(MergeTargetCandidateViewModel.IsSelected));
        sut.IsSelected.Should().BeTrue();
    }

    [Fact]
    public void SortKey_ReflectsModelUpdatedAt()
    {
        var clock = new SessionsViewModelTests.FixedTimeProvider(Now);
        var sut = new MergeTargetCandidateViewModel(Card(updatedAt: Now.AddMinutes(-7)), clock);
        sut.SortKey.Should().Be(Now.AddMinutes(-7));
    }
}
