using CopilotSessionManager.Core.Cli;
using CopilotSessionManager.Core.Cost;
using CopilotSessionManager.Core.Models;
using FluentAssertions;
using Xunit;

namespace CopilotSessionManager.Core.Tests.Cost;

public class ModelCostCalculatorTests
{
    private readonly ModelCostCalculator _sut = new(new EmbeddedModelCatalog());

    [Fact]
    public void Estimate_NullInfo_ReturnsNull()
    {
        _sut.Estimate(null).Should().BeNull();
    }

    [Fact]
    public void Estimate_EmptyUsage_ReturnsNull()
    {
        var info = new SessionModelInfo("claude-opus-4.6", IsFromShutdown: false,
            new Dictionary<string, ModelUsage>(StringComparer.Ordinal));
        _sut.Estimate(info).Should().BeNull();
    }

    [Fact]
    public void Estimate_KnownModel_ComputesExpectedCost()
    {
        // Opus 4.6 rates: input 15, output 75, cacheRead 1.5 per 1M.
        // 1M input + 100k output + 500k cache reads.
        var usage = new ModelUsage(
            InputTokens: 1_000_000,
            OutputTokens: 100_000,
            CacheReadTokens: 500_000,
            CacheWriteTokens: 0,
            ReasoningTokens: 0,
            RequestCount: 10);
        var info = new SessionModelInfo("claude-opus-4.6", true,
            new Dictionary<string, ModelUsage>(StringComparer.Ordinal) { ["claude-opus-4.6"] = usage });

        var result = _sut.Estimate(info)!;
        // 15 + 7.5 + 0.75 = 23.25
        result.UsdAmount.Should().Be(23.25m);
        result.HasUnknownModels.Should().BeFalse();
    }

    [Fact]
    public void Estimate_MultipleModels_SumsContributions()
    {
        var info = new SessionModelInfo("claude-sonnet-4.6", true,
            new Dictionary<string, ModelUsage>(StringComparer.Ordinal)
            {
                ["claude-sonnet-4.6"] = new(2_000_000, 200_000, 0, 0, 0, 5), // 2*3 + 0.2*15 = 9
                ["claude-haiku-4.5"] = new(1_000_000, 100_000, 0, 0, 0, 3), // 1 + 0.5 = 1.5
            });

        var result = _sut.Estimate(info)!;
        result.UsdAmount.Should().Be(10.5m);
        result.HasUnknownModels.Should().BeFalse();
    }

    [Fact]
    public void Estimate_UnknownModel_ContributesNothing_AndFlagSet()
    {
        var info = new SessionModelInfo("???", true,
            new Dictionary<string, ModelUsage>(StringComparer.Ordinal)
            {
                ["claude-haiku-4.5"] = new(1_000_000, 0, 0, 0, 0, 1),
                ["mystery-model-9000"] = new(99_999_999, 99_999_999, 0, 0, 0, 99),
            });

        var result = _sut.Estimate(info)!;
        result.UsdAmount.Should().Be(1.0m);
        result.HasUnknownModels.Should().BeTrue();
    }

    [Fact]
    public void Estimate_OnlyUnknownModels_ReturnsZeroWithFlag()
    {
        var info = new SessionModelInfo("?", true,
            new Dictionary<string, ModelUsage>(StringComparer.Ordinal)
            {
                ["mystery"] = new(1_000_000, 1_000_000, 0, 0, 0, 1),
            });

        var result = _sut.Estimate(info)!;
        result.UsdAmount.Should().Be(0m);
        result.HasUnknownModels.Should().BeTrue();
    }

    [Fact]
    public void Estimate_ZeroUsage_ReturnsZero()
    {
        var info = new SessionModelInfo("claude-haiku-4.5", true,
            new Dictionary<string, ModelUsage>(StringComparer.Ordinal)
            {
                ["claude-haiku-4.5"] = ModelUsage.Zero,
            });

        var result = _sut.Estimate(info)!;
        result.UsdAmount.Should().Be(0m);
        result.HasUnknownModels.Should().BeFalse();
    }
}
