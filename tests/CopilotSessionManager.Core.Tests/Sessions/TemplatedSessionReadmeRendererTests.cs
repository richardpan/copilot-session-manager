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

    // ---- V1.3 auto sections ----------------------------------------------

    [Fact]
    public void Render_IncludesV13AutoSections_BetweenHistoryAndNotes()
    {
        var output = Sut().Render(Ctx(BuildSession()));

        var history = output.IndexOf("## History", StringComparison.Ordinal);
        var prompts = output.IndexOf("## Recent prompts", StringComparison.Ordinal);
        var tools = output.IndexOf("## Tool usage", StringComparison.Ordinal);
        var subagents = output.IndexOf("## Sub-agents", StringComparison.Ordinal);
        var gaps = output.IndexOf("## Activity gaps", StringComparison.Ordinal);
        var notes = output.IndexOf("## Notes", StringComparison.Ordinal);

        history.Should().BeGreaterThan(0);
        prompts.Should().BeGreaterThan(history);
        tools.Should().BeGreaterThan(prompts);
        subagents.Should().BeGreaterThan(tools);
        gaps.Should().BeGreaterThan(subagents);
        notes.Should().BeGreaterThan(gaps);
    }

    [Fact]
    public void Render_RecentPromptsSection_FallsBack_WhenEmpty()
    {
        var output = Sut().Render(Ctx(BuildSession()));
        output.Should().Contain("## Recent prompts");
        output.Should().Contain("_(No user prompts recorded yet.)_");
    }

    [Fact]
    public void Render_RecentPromptsSection_ListsBodiesWithTimestamp()
    {
        var summary = new SessionEventSummary(
            new[]
            {
                new RecentPrompt(new DateTimeOffset(2026, 5, 8, 11, 50, 0, TimeSpan.Zero), "ship the v1.3 work"),
                new RecentPrompt(new DateTimeOffset(2026, 5, 8, 11, 30, 0, TimeSpan.Zero), "what should we do?"),
            },
            Array.Empty<ToolUsageCount>(),
            null, null, 2);

        var output = Sut().Render(WithSummary(BuildSession(), summary));

        output.Should().Contain("ship the v1.3 work");
        output.Should().Contain("what should we do?");
        // Timestamp formatted with the "u" specifier
        output.Should().Contain("2026-05-08 11:50:00Z");
    }

    [Fact]
    public void Render_ToolUsageSection_FallsBack_WhenEmpty()
    {
        var output = Sut().Render(Ctx(BuildSession()));
        output.Should().Contain("## Tool usage");
        output.Should().Contain("_(No tool calls recorded yet.)_");
    }

    [Fact]
    public void Render_ToolUsageSection_RendersHistogramTable()
    {
        var summary = new SessionEventSummary(
            Array.Empty<RecentPrompt>(),
            new[]
            {
                new ToolUsageCount("grep", 12),
                new ToolUsageCount("view", 7),
            },
            null, null, 19);

        var output = Sut().Render(WithSummary(BuildSession(), summary));

        output.Should().Contain("| Tool | Count |");
        output.Should().Contain("| `grep` | 12 |");
        output.Should().Contain("| `view` | 7 |");
    }

    [Fact]
    public void Render_SubagentsSection_FallsBack_WhenEmpty()
    {
        var output = Sut().Render(Ctx(BuildSession()));
        output.Should().Contain("## Sub-agents");
        output.Should().Contain("_(No sub-agents launched.)_");
    }

    [Fact]
    public void Render_SubagentsSection_RendersTableRow()
    {
        var sa = new SubagentSummary(
            ToolCallId: "tc-1",
            Name: "explore",
            AgentType: "explore",
            AgentDisplayName: "Codebase Explorer",
            Model: "claude-haiku-4.5",
            TokensTotal: 12_345,
            ToolCallsTotal: 4,
            Duration: TimeSpan.FromSeconds(45),
            StartedAt: new DateTimeOffset(2026, 5, 8, 11, 40, 0, TimeSpan.Zero),
            CompletedAt: new DateTimeOffset(2026, 5, 8, 11, 41, 0, TimeSpan.Zero),
            Status: SubagentStatus.Completed);

        var output = Sut().Render(WithSubagents(BuildSession(), sa));

        output.Should().Contain("| Started | Name | Type | Status | Tokens | Duration |");
        output.Should().Contain("Codebase Explorer");
        output.Should().Contain("explore");
        output.Should().Contain("Completed");
    }

    [Fact]
    public void Render_ActivityGapsSection_FallsBack_WhenNoEvents()
    {
        var output = Sut().Render(Ctx(BuildSession()));
        output.Should().Contain("## Activity gaps");
        output.Should().Contain("_(No events recorded yet.)_");
    }

    [Fact]
    public void Render_ActivityGapsSection_FormatsSpansAndCounts()
    {
        var summary = new SessionEventSummary(
            Array.Empty<RecentPrompt>(),
            Array.Empty<ToolUsageCount>(),
            LongestIdleGap: TimeSpan.FromMinutes(45),
            TotalActiveSpan: TimeSpan.FromHours(2) + TimeSpan.FromMinutes(15),
            TotalEvents: 132);

        var output = Sut().Render(WithSummary(BuildSession(), summary));

        output.Should().Contain("**Total events:** 132");
        output.Should().Contain("**Total active span:** 2h 15m");
        output.Should().Contain("**Longest idle gap:** 45m 0s");
    }

    [Fact]
    public void Render_PromptBodyWithMarkdownChars_IsLineCollapsedNotEscaped()
    {
        // The renderer collapses CR/LF to spaces but otherwise leaves the
        // body intact (markdown injection is a non-issue for a local-only
        // doc — we just need predictable layout).
        var summary = new SessionEventSummary(
            new[] { new RecentPrompt(new DateTimeOffset(2026, 5, 8, 12, 0, 0, TimeSpan.Zero), "fix `bug`\nand ship") },
            Array.Empty<ToolUsageCount>(),
            null, null, 1);

        var output = Sut().Render(WithSummary(BuildSession(), summary));

        output.Should().Contain("fix `bug` and ship");
    }

    private static SessionReadmeContext WithSummary(Session session, SessionEventSummary summary) =>
        new(session, SessionType.Bug, Array.Empty<SessionCheckpointSummary>(),
            summary, Array.Empty<SubagentSummary>());

    private static SessionReadmeContext WithSubagents(Session session, params SubagentSummary[] subagents) =>
        new(session, SessionType.Bug, Array.Empty<SessionCheckpointSummary>(),
            SessionEventSummary.Empty, subagents);

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public FixedTimeProvider(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
