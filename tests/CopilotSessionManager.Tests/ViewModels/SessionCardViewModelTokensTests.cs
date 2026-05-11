using System;
using System.Collections.Generic;
using CopilotSessionManager.Core.Models;
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
    [InlineData(1L, "1")]
    [InlineData(999L, "999")]
    [InlineData(1000L, "1.0k")]
    [InlineData(1234L, "1.2k")]
    [InlineData(9999L, "10.0k")]
    [InlineData(10_000L, "10k")]
    [InlineData(12_345L, "12k")]
    [InlineData(999_999L, "999k")]
    [InlineData(1_000_000L, "1.0M")]
    [InlineData(1_234_567L, "1.2M")]
    [InlineData(12_345_678L, "12.3M")]
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
        sut.TokensDisplay.Should().Be("12k");
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
}
