using System;
using System.Collections.Generic;
using CopilotSessionManager.Core.Models;
using CopilotSessionManager.Core.Sessions;
using FluentAssertions;
using Xunit;

namespace CopilotSessionManager.Core.Tests.Sessions;

public class TemplatedSessionReadmeRendererTests
{
    private static readonly DateTimeOffset Now = new(2026, 5, 8, 12, 0, 0, TimeSpan.Zero);

    private static Session BuildSession(
        string id = "abcdef0123456789",
        string? summary = "Investigate flaky CI on Windows runners",
        string? repo = "richardpan/csm",
        string? branch = "main",
        string? cwd = @"C:\ws\csm",
        int turns = 7,
        SessionStatus status = SessionStatus.Idle,
        IReadOnlyList<SessionLockInfo>? locks = null) =>
        new(
            Id: id,
            Cwd: cwd,
            Repository: repo,
            Branch: branch,
            Summary: summary,
            HostType: "vscode",
            CreatedAt: new DateTimeOffset(2026, 5, 8, 11, 30, 0, TimeSpan.Zero),
            UpdatedAt: new DateTimeOffset(2026, 5, 8, 11, 55, 0, TimeSpan.Zero),
            TurnCount: turns,
            Status: status,
            CopilotVersion: new CopilotVersion(1, 0, 44),
            Locks: locks ?? Array.Empty<SessionLockInfo>());

    private static SessionReadmeContext Ctx(
        Session session,
        SessionType label = SessionType.Bug,
        IReadOnlyList<SessionCheckpointSummary>? checkpoints = null) =>
        new(session, label, checkpoints ?? Array.Empty<SessionCheckpointSummary>());

    private static TemplatedSessionReadmeRenderer Sut() =>
        new(new FixedTimeProvider(Now));

    [Fact]
    public void Render_IncludesAllSections_InOrder()
    {
        var output = Sut().Render(Ctx(BuildSession()));

        output.IndexOf("## Overview", StringComparison.Ordinal)
            .Should().BeLessThan(output.IndexOf("## Activity", StringComparison.Ordinal));
        output.IndexOf("## Activity", StringComparison.Ordinal)
            .Should().BeLessThan(output.IndexOf("## Goal", StringComparison.Ordinal));
        output.IndexOf("## Goal", StringComparison.Ordinal)
            .Should().BeLessThan(output.IndexOf("## History", StringComparison.Ordinal));
        output.IndexOf("## History", StringComparison.Ordinal)
            .Should().BeLessThan(output.IndexOf("## Notes", StringComparison.Ordinal));
        output.IndexOf("## Notes", StringComparison.Ordinal)
            .Should().BeLessThan(output.IndexOf("## Next steps", StringComparison.Ordinal));
    }

    [Fact]
    public void Render_TitleUsesSummary_WhenAvailable()
    {
        var output = Sut().Render(Ctx(BuildSession(summary: "Add session labels")));
        output.Should().StartWith("# Add session labels");
    }

    [Fact]
    public void Render_TitleFallsBackToShortId_WhenNoSummary()
    {
        var output = Sut().Render(Ctx(BuildSession(summary: null)));
        output.Should().StartWith("# Session abcdef01");
    }

    [Fact]
    public void Render_OverviewIncludesLabelStatusRepoBranchCwd()
    {
        var output = Sut().Render(Ctx(BuildSession(), SessionType.Refactor));

        output.Should().Contain("**Label:** Refactor");
        output.Should().Contain("**Status:** Idle");
        output.Should().Contain("**Repository:** richardpan/csm");
        output.Should().Contain("**Branch:** main");
        output.Should().Contain("**Working directory:** `C:\\ws\\csm`");
        output.Should().Contain("**Session id:** `abcdef0123456789`");
    }

    [Fact]
    public void Render_OverviewHandlesMissingMetadata()
    {
        var output = Sut().Render(Ctx(BuildSession(repo: null, branch: null, cwd: null)));

        output.Should().Contain("**Repository:** _(none)_");
        output.Should().Contain("**Branch:** _(none)_");
        output.Should().Contain("**Working directory:** _(none)_");
    }

    [Fact]
    public void Render_ActivityShowsTurnsAndLocks()
    {
        var locks = new[]
        {
            new SessionLockInfo("C:/x.lock", 1234, true),
            new SessionLockInfo("C:/y.lock", 5678, false),
        };
        var output = Sut().Render(Ctx(BuildSession(turns: 42, locks: locks)));

        output.Should().Contain("**Turns:** 42");
        output.Should().Contain("**Active locks:** PID 1234, PID 5678");
    }

    [Fact]
    public void Render_GoalSection_FallsBackWhenSummaryMissing()
    {
        var output = Sut().Render(Ctx(BuildSession(summary: null)));
        output.Should().Contain("_(No summary recorded by the CLI yet.)_");
    }

    [Fact]
    public void Render_HistorySection_ListsCheckpointsInGivenOrder()
    {
        var checkpoints = new[]
        {
            new SessionCheckpointSummary(1, "Planning the app", "/one.md"),
            new SessionCheckpointSummary(2, "Implementing CLI adapter", "/two.md"),
        };
        var output = Sut().Render(Ctx(BuildSession(), SessionType.Bug, checkpoints));

        var idxOne = output.IndexOf("**001**", StringComparison.Ordinal);
        var idxTwo = output.IndexOf("**002**", StringComparison.Ordinal);
        idxOne.Should().BeGreaterThan(0);
        idxTwo.Should().BeGreaterThan(idxOne);
        output.Should().Contain("Planning the app");
        output.Should().Contain("Implementing CLI adapter");
    }

    [Fact]
    public void Render_HistorySection_FallsBackWhenNoCheckpoints()
    {
        var output = Sut().Render(Ctx(BuildSession()));
        output.Should().Contain("_(No checkpoints recorded.)_");
    }

    [Fact]
    public void Render_EmitsUserBlockMarkers_ForAllUserSections()
    {
        var output = Sut().Render(Ctx(BuildSession()));

        foreach (var name in TemplatedSessionReadmeRenderer.UserBlockNames)
        {
            output.Should().Contain($"<!-- USER:BEGIN {name} -->");
            output.Should().Contain($"<!-- USER:END {name} -->");
        }
    }

    [Fact]
    public void Render_IsDeterministic_ForSameInputAndClock()
    {
        var sut = Sut();
        var session = BuildSession();
        sut.Render(Ctx(session)).Should().Be(sut.Render(Ctx(session)));
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public FixedTimeProvider(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
