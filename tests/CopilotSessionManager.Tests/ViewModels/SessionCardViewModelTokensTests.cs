using System;
using System.Collections.Generic;
using CopilotSessionManager.Core.Models;
using CopilotSessionManager.Core.Sessions;
using CopilotSessionManager.ViewModels;
using FluentAssertions;
using Xunit;

namespace CopilotSessionManager.Tests.ViewModels;

/// <summary>
/// V1.5 — token-aggregate column on the data-table layout (#116).
/// </summary>
public class SessionCardViewModelTokensTests
{
    private static readonly DateTimeOffset Now = new(2026, 5, 11, 12, 0, 0, TimeSpan.Zero);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static Session SessionWithUsage(params (string ModelId, long Total)[] perModel)
    {
        var dict = new Dictionary<string, ModelUsage>(StringComparer.Ordinal);
        foreach (var (id, total) in perModel)
        {
            dict[id] = new ModelUsage(
                InputTokens: total,
                OutputTokens: 0,
                CacheReadTokens: 0,
                CacheWriteTokens: 0,
                ReasoningTokens: 0,
                RequestCount: 1);
        }
        var info = new SessionModelInfo("claude-sonnet-4.6", IsFromShutdown: true, dict);
        return new Session(
            Id: "abcdef1234567890",
            Cwd: @"C:\ws\repo",
            Repository: "owner/repo",
            Branch: "main",
            Summary: "Tokens fixture",
            HostType: "cli",
            CreatedAt: Now.AddHours(-1),
            UpdatedAt: Now.AddMinutes(-2),
            TurnCount: 5,
            Status: SessionStatus.Idle,
            CopilotVersion: CopilotVersion.Zero,
            Locks: Array.Empty<SessionLockInfo>(),
            ModelInfo: info);
    }

    private static Session SessionWithoutUsage() => new(
        Id: "abcdef1234567890",
        Cwd: @"C:\ws\repo",
        Repository: "owner/repo",
        Branch: "main",
        Summary: "No usage",
        HostType: "cli",
        CreatedAt: Now.AddHours(-1),
        UpdatedAt: Now.AddMinutes(-2),
        TurnCount: 0,
        Status: SessionStatus.Idle,
        CopilotVersion: CopilotVersion.Zero,
        Locks: Array.Empty<SessionLockInfo>(),
        ModelInfo: null);

    private static SubagentSummary Subagent(string id, long tokens) => new(
        ToolCallId: id,
        Name: id,
        AgentType: "task",
        AgentDisplayName: id,
        Model: "claude-sonnet-4.6",
        TokensTotal: tokens,
        ToolCallsTotal: 2,
        Duration: TimeSpan.FromSeconds(3),
        StartedAt: Now.AddMinutes(-1),
        CompletedAt: Now,
        Status: SubagentStatus.Completed);

    [Fact]
    public void TokensDisplay_NullModelInfo_ReturnsEmDash()
    {
        var sut = new SessionCardViewModel(SessionWithoutUsage(), new FixedTimeProvider(Now));

        sut.TokensDisplay.Should().Be("—");
        sut.TotalTokensRaw.Should().Be(0);
    }

    [Fact]
    public void TokensDisplay_EmptyUsageDictionary_ReturnsEmDash()
    {
        var sut = new SessionCardViewModel(SessionWithUsage(), new FixedTimeProvider(Now));

        sut.TokensDisplay.Should().Be("—");
    }

    [Fact]
    public void TokensDisplay_ZeroTokensAcrossModels_ReturnsEmDash()
    {
        var sut = new SessionCardViewModel(SessionWithUsage(("claude", 0L), ("gpt", 0L)), new FixedTimeProvider(Now));

        sut.TokensDisplay.Should().Be("—");
    }

    [Theory]
    [InlineData(1L, "1 / —")]
    [InlineData(999L, "999 / —")]
    [InlineData(1000L, "1.0k / —")]
    [InlineData(1234L, "1.2k / —")]
    [InlineData(9999L, "10.0k / —")]
    [InlineData(10_000L, "10k / —")]
    [InlineData(12_345L, "12k / —")]
    [InlineData(999_999L, "999k / —")]
    [InlineData(1_000_000L, "1.0M / —")]
    [InlineData(1_234_567L, "1.2M / —")]
    [InlineData(12_345_678L, "12.3M / —")]
    public void TokensDisplay_FormatsCorrectly(long total, string expected)
    {
        var sut = new SessionCardViewModel(SessionWithUsage(("claude", total)), new FixedTimeProvider(Now));

        sut.TokensDisplay.Should().Be(expected);
        sut.TotalTokensRaw.Should().Be(total);
    }

    [Fact]
    public void TokensDisplay_SumsAcrossModels()
    {
        var sut = new SessionCardViewModel(
            SessionWithUsage(("claude", 5_000L), ("gpt", 7_000L), ("haiku", 500L)),
            new FixedTimeProvider(Now));

        sut.TotalTokensRaw.Should().Be(12_500L);
        sut.TokensDisplay.Should().Be("12k / —");
    }

    [Fact]
    public void TokensTooltip_NoData_ExplainsWhenAvailable()
    {
        var sut = new SessionCardViewModel(SessionWithoutUsage(), new FixedTimeProvider(Now));

        sut.TokensTooltip.Should().Contain("only available after the session ends");
    }

    [Fact]
    public void TokensTooltip_WithData_IncludesAbsoluteCountAndModelCount()
    {
        var sut = new SessionCardViewModel(
            SessionWithUsage(("claude", 1_234_567L), ("gpt", 100L)),
            new FixedTimeProvider(Now));

        sut.TokensTooltip.Should().Contain("1,234,667 tokens");
        sut.TokensTooltip.Should().Contain("2 models");
        sut.TokensTooltip.Should().Contain("shutdown record");
    }

    [Fact]
    public void TokensDisplay_WithSubagents_AppendsRollupSuffix()
    {
        var sut = new SessionCardViewModel(SessionWithUsage(("claude", 1_234_567L)), new FixedTimeProvider(Now));

        sut.SetSubagents(new[] { Subagent("call-1", 5_200_000L) });

        sut.TokensDisplay.Should().Be("1.2M / — (+5.2M)");
    }

    [Fact]
    public void TokensDisplay_WithoutParentTokensButWithSubagents_RendersDashPlusRollup()
    {
        var sut = new SessionCardViewModel(SessionWithoutUsage(), new FixedTimeProvider(Now));

        sut.SetSubagents(new[] { Subagent("call-1", 5_200_000L) });

        sut.TokensDisplay.Should().Be("— (+5.2M)");
    }

    [Fact]
    public void SubagentDerivedProperties_ReflectAssignedSubagents()
    {
        var sut = new SessionCardViewModel(SessionWithUsage(("claude", 100L)), new FixedTimeProvider(Now));

        sut.SetSubagents(new[] { Subagent("a", 1_000L), Subagent("b", 2_000L) });

        sut.HasSubagents.Should().BeTrue();
        sut.SubagentCount.Should().Be(2);
        sut.SubagentTokensTotal.Should().Be(3_000L);
        sut.SubagentTokensDisplay.Should().Be("3.0k");
    }

    [Fact]
    public void TotalTokensRaw_IncludesSubagentTokens()
    {
        var sut = new SessionCardViewModel(SessionWithUsage(("claude", 1_000L)), new FixedTimeProvider(Now));

        sut.SetSubagents(new[] { Subagent("call-1", 2_500L) });

        sut.TotalTokensRaw.Should().Be(3_500L);
    }

    [Fact]
    public void SetSubagents_RaisesDerivedPropertyNotifications()
    {
        var sut = new SessionCardViewModel(SessionWithUsage(("claude", 1_000L)), new FixedTimeProvider(Now));
        var changed = new HashSet<string>();
        sut.PropertyChanged += (_, e) => changed.Add(e.PropertyName!);

        sut.SetSubagents(new[] { Subagent("call-1", 2_500L) });

        changed.Should().Contain(new[]
        {
            nameof(SessionCardViewModel.Subagents),
            nameof(SessionCardViewModel.HasSubagents),
            nameof(SessionCardViewModel.SubagentCount),
            nameof(SessionCardViewModel.SubagentTokensTotal),
            nameof(SessionCardViewModel.SubagentBadgeText),
            nameof(SessionCardViewModel.SubagentTokensDisplay),
            nameof(SessionCardViewModel.TokensDisplay),
            nameof(SessionCardViewModel.TotalTokensRaw),
            nameof(SessionCardViewModel.TokensTooltip),
        });
    }

    [Fact]
    public void SubagentBadgeText_IsEmptyUntilSubagentsAreLoaded()
    {
        var sut = new SessionCardViewModel(SessionWithoutUsage(), new FixedTimeProvider(Now));

        sut.SubagentBadgeText.Should().BeEmpty();

        sut.SetSubagents(new[] { Subagent("a", 1L), Subagent("b", 1L), Subagent("c", 1L) });

        sut.SubagentBadgeText.Should().Be("🧰 ×3");
    }

    [Fact]
    public void TokensTooltip_WithSubagents_IncludesBreakdown()
    {
        var sut = new SessionCardViewModel(SessionWithUsage(("claude", 1_000L)), new FixedTimeProvider(Now));

        sut.SetSubagents(new[] { Subagent("a", 2_000L), Subagent("b", 4_000L) });

        sut.TokensTooltip.Should().Contain("+ 2 sub-agents totalling 6.0k tokens (3.0k avg)");
    }

    [Fact]
    public void SubagentTokensDisplay_WithoutSubagents_ReturnsDash()
    {
        var sut = new SessionCardViewModel(SessionWithoutUsage(), new FixedTimeProvider(Now));

        sut.SubagentTokensDisplay.Should().Be("—");
    }

    [Fact]
    public void SetSubagents_NullList_TreatsAsEmpty()
    {
        var sut = new SessionCardViewModel(SessionWithoutUsage(), new FixedTimeProvider(Now));

        sut.SetSubagents(null!);

        sut.HasSubagents.Should().BeFalse();
        sut.Subagents.Should().BeEmpty();
    }

    [Fact]
    public async Task LoadSubagentsAsync_ScansOnlyOnceAfterSuccess()
    {
        var sut = new SessionCardViewModel(SessionWithoutUsage(), new FixedTimeProvider(Now));
        var scanner = new FakeSubagentScanService(new[] { Subagent("call-1", 42L) });

        await sut.LoadSubagentsAsync(scanner);
        await sut.LoadSubagentsAsync(scanner);

        scanner.CallCount.Should().Be(1);
        sut.Subagents.Should().ContainSingle().Which.TokensTotal.Should().Be(42L);
    }

    private sealed class FakeSubagentScanService(IReadOnlyList<SubagentSummary> result) : ISubagentScanService
    {
        public int CallCount { get; private set; }

        public Task<IReadOnlyList<SubagentSummary>> ScanAsync(string sessionId, CancellationToken ct = default)
        {
            CallCount++;
            return Task.FromResult(result);
        }
    }
}
