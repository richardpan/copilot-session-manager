using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows.Media;
using CopilotSessionManager.Core.Models;
using CopilotSessionManager.ViewModels;
using FluentAssertions;
using Xunit;

namespace CopilotSessionManager.Tests.ViewModels;

public class SessionCardViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 5, 8, 12, 0, 0, TimeSpan.Zero);

    private static FixedTimeProvider TimeAt(DateTimeOffset when) => new(when);

    private static Session BuildSession(
        string id = "abcdef1234567890",
        SessionStatus status = SessionStatus.Idle,
        string? summary = "Hello world",
        string? repo = "owner/repo",
        string? branch = "main",
        DateTimeOffset? updatedAt = null,
        DateTimeOffset? createdAt = null,
        int turnCount = 3,
        IReadOnlyList<SessionLockInfo>? locks = null) =>
        new(
            Id: id,
            Cwd: @"C:\ws\repo",
            Repository: repo,
            Branch: branch,
            Summary: summary,
            HostType: "cli",
            CreatedAt: createdAt ?? Now.AddMinutes(-30),
            UpdatedAt: updatedAt ?? Now.AddMinutes(-2),
            TurnCount: turnCount,
            Status: status,
            CopilotVersion: CopilotVersion.Zero,
            Locks: locks ?? Array.Empty<SessionLockInfo>());

    [Fact]
    public void ShortId_TakesFirstEightCharacters()
    {
        var sut = new SessionCardViewModel(BuildSession(id: "abcdef1234567890"), TimeAt(Now));
        sut.ShortId.Should().Be("abcdef12");
    }

    [Fact]
    public void Title_PrefersSummaryThenRepositoryThenShortId()
    {
        new SessionCardViewModel(BuildSession(summary: "  ", repo: "owner/repo"), TimeAt(Now))
            .Title.Should().Be("owner/repo");
        new SessionCardViewModel(BuildSession(summary: "  ", repo: null, id: "abcdef1234"), TimeAt(Now))
            .Title.Should().Be("abcdef12");
        new SessionCardViewModel(BuildSession(summary: "Pick me"), TimeAt(Now))
            .Title.Should().Be("Pick me");
    }

    [Theory]
    [InlineData(SessionStatus.Working, "Working")]
    [InlineData(SessionStatus.AwaitingApproval, "Awaiting approval")]
    [InlineData(SessionStatus.AwaitingInput, "Awaiting input")]
    [InlineData(SessionStatus.Idle, "Idle")]
    [InlineData(SessionStatus.Inactive, "Inactive")]
    [InlineData(SessionStatus.Orphaned, "Crashed")]
    public void StatusLabel_MatchesStatus(SessionStatus status, string expected)
    {
        new SessionCardViewModel(BuildSession(status: status), TimeAt(Now))
            .StatusLabel.Should().Be(expected);
    }

    [Theory]
    [InlineData(SessionStatus.Working)]
    [InlineData(SessionStatus.AwaitingApproval)]
    [InlineData(SessionStatus.AwaitingInput)]
    [InlineData(SessionStatus.Idle)]
    [InlineData(SessionStatus.Inactive)]
    [InlineData(SessionStatus.Orphaned)]
    public void StatusBrush_IsFrozenSolidColor(SessionStatus status)
    {
        var brush = new SessionCardViewModel(BuildSession(status: status), TimeAt(Now)).StatusBrush;
        brush.Should().BeOfType<SolidColorBrush>();
    }

    [Fact]
    public void StatusBrush_DiffersAcrossStatuses()
    {
        var working = new SessionCardViewModel(BuildSession(status: SessionStatus.Working), TimeAt(Now)).StatusBrush;
        var orphaned = new SessionCardViewModel(BuildSession(status: SessionStatus.Orphaned), TimeAt(Now)).StatusBrush;
        ((SolidColorBrush)working).Color.Should().NotBe(((SolidColorBrush)orphaned).Color);
    }

    [Fact]
    public void StatusBrush_Idle_IsGoldYellow()
    {
        // Per the QoL change after v1.3.0: Idle reads as Gold (#FFD700) so a
        // long-quiet-but-still-locked session draws the eye, while staying
        // visually distinct from the Goldenrod (#DAA520) used for
        // AwaitingApproval.
        var idle = new SessionCardViewModel(BuildSession(status: SessionStatus.Idle), TimeAt(Now)).StatusBrush;
        var awaitingApproval = new SessionCardViewModel(BuildSession(status: SessionStatus.AwaitingApproval), TimeAt(Now)).StatusBrush;

        ((SolidColorBrush)idle).Color.Should().Be(Colors.Gold);
        ((SolidColorBrush)idle).Color.Should().NotBe(((SolidColorBrush)awaitingApproval).Color);
    }

    [Theory]
    [InlineData(0, "just now")]
    [InlineData(30, "just now")]
    [InlineData(90, "1 min ago")]
    [InlineData(60 * 90, "1 hr ago")]
    [InlineData(60 * 60 * 30, "1 d ago")]
    public void UpdatedRelative_BucketsByDelta(int deltaSeconds, string expected)
    {
        var session = BuildSession(updatedAt: Now.AddSeconds(-deltaSeconds));
        new SessionCardViewModel(session, TimeAt(Now)).UpdatedRelative.Should().Be(expected);
    }

    [Fact]
    public void LockSummary_HandlesZeroOneAndMany()
    {
        new SessionCardViewModel(BuildSession(locks: Array.Empty<SessionLockInfo>()), TimeAt(Now))
            .LockSummary.Should().Be("no locks");

        new SessionCardViewModel(
            BuildSession(locks: new[] { new SessionLockInfo("c:\\l.lock", 1234, true) }),
            TimeAt(Now)).LockSummary.Should().Be("PID 1234");

        new SessionCardViewModel(
            BuildSession(locks: new[] { new SessionLockInfo("c:\\l.lock", 1234, false) }),
            TimeAt(Now)).LockSummary.Should().Be("PID 1234 (dead)");

        new SessionCardViewModel(
            BuildSession(locks: new[]
            {
                new SessionLockInfo("c:\\a.lock", 1, true),
                new SessionLockInfo("c:\\b.lock", 2, true),
            }),
            TimeAt(Now)).LockSummary.Should().Be("2 locks");
    }

    [Fact]
    public void UpdateFrom_RaisesChangeNotificationsForProjectedProperties()
    {
        var sut = new SessionCardViewModel(BuildSession(status: SessionStatus.Idle), TimeAt(Now));
        var changed = new List<string>();
        sut.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is not null)
            {
                changed.Add(e.PropertyName);
            }
        };

        sut.UpdateFrom(BuildSession(status: SessionStatus.Working, summary: "New title", turnCount: 7));

        changed.Should().Contain(new[]
        {
            nameof(SessionCardViewModel.Title),
            nameof(SessionCardViewModel.Status),
            nameof(SessionCardViewModel.StatusLabel),
            nameof(SessionCardViewModel.StatusBrush),
            nameof(SessionCardViewModel.TurnCount),
            nameof(SessionCardViewModel.UpdatedRelative),
        });
        sut.Title.Should().Be("New title");
        sut.Status.Should().Be(SessionStatus.Working);
        sut.TurnCount.Should().Be(7);
    }

    [Fact]
    public void UpdateFrom_DifferentId_Throws()
    {
        var sut = new SessionCardViewModel(BuildSession(id: "aaaaaaaa11111111"), TimeAt(Now));
        var act = () => sut.UpdateFrom(BuildSession(id: "bbbbbbbb22222222"));
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Constructors_RejectNullArgs()
    {
        Action a = () => new SessionCardViewModel(null!);
        a.Should().Throw<ArgumentNullException>();
        Action b = () => new SessionCardViewModel(BuildSession(), null!);
        b.Should().Throw<ArgumentNullException>();
    }

    [Theory]
    [InlineData(SessionType.Exploratory, "Exploratory")]
    [InlineData(SessionType.Research, "Research")]
    [InlineData(SessionType.Feature, "Feature")]
    [InlineData(SessionType.Bug, "Bug")]
    [InlineData(SessionType.Refactor, "Refactor")]
    [InlineData(SessionType.Docs, "Docs")]
    [InlineData(SessionType.Infra, "Infra")]
    [InlineData(SessionType.Experiment, "Experiment")]
    public void LabelText_MatchesEnum(SessionType type, string expected)
    {
        var sut = new SessionCardViewModel(BuildSession(), type, TimeAt(Now));
        sut.LabelText.Should().Be(expected);
    }

    [Fact]
    public void LabelBrush_DiffersAcrossTypes()
    {
        var bug = new SessionCardViewModel(BuildSession(), SessionType.Bug, TimeAt(Now)).LabelBrush;
        var feat = new SessionCardViewModel(BuildSession(), SessionType.Feature, TimeAt(Now)).LabelBrush;
        ((SolidColorBrush)bug).Color.Should().NotBe(((SolidColorBrush)feat).Color);
    }

    [Fact]
    public void DefaultLabel_IsExploratory()
    {
        new SessionCardViewModel(BuildSession()).Label.Should().Be(SessionType.Exploratory);
        new SessionCardViewModel(BuildSession(), TimeAt(Now)).Label.Should().Be(SessionType.Exploratory);
    }

    [Fact]
    public void UpdateLabel_RaisesChangeNotifications()
    {
        var sut = new SessionCardViewModel(BuildSession(), SessionType.Exploratory, TimeAt(Now));
        var changed = new List<string>();
        sut.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is not null)
            {
                changed.Add(e.PropertyName);
            }
        };

        sut.UpdateLabel(SessionType.Bug);

        changed.Should().Contain(new[]
        {
            nameof(SessionCardViewModel.Label),
            nameof(SessionCardViewModel.LabelText),
            nameof(SessionCardViewModel.LabelBrush),
        });
        sut.Label.Should().Be(SessionType.Bug);
    }

    [Fact]
    public void UpdateLabel_SameValue_NoOp()
    {
        var sut = new SessionCardViewModel(BuildSession(), SessionType.Bug, TimeAt(Now));
        var fired = 0;
        sut.PropertyChanged += (_, _) => fired++;

        sut.UpdateLabel(SessionType.Bug);

        fired.Should().Be(0);
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public FixedTimeProvider(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
