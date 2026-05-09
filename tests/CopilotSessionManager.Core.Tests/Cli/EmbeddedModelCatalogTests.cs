using CopilotSessionManager.Core.Cli;
using CopilotSessionManager.Core.Models;
using FluentAssertions;
using Xunit;

namespace CopilotSessionManager.Core.Tests.Cli;

public class EmbeddedModelCatalogTests
{
    private readonly EmbeddedModelCatalog _sut = new();

    [Theory]
    [InlineData("claude-opus-4.7", ModelTier.Premium)]
    [InlineData("claude-opus-4.6", ModelTier.Premium)]
    [InlineData("claude-opus-4.5", ModelTier.Premium)]
    [InlineData("claude-sonnet-4.6", ModelTier.Standard)]
    [InlineData("claude-sonnet-4.5", ModelTier.Standard)]
    [InlineData("claude-haiku-4.5", ModelTier.Fast)]
    [InlineData("gpt-5.5", ModelTier.Premium)]
    [InlineData("gpt-5.4", ModelTier.Standard)]
    [InlineData("gpt-5.4-mini", ModelTier.Fast)]
    [InlineData("gpt-5-mini", ModelTier.Fast)]
    [InlineData("gpt-4.1", ModelTier.Fast)]
    public void Resolve_KnownIds_ReturnsExpectedTier(string id, ModelTier tier)
    {
        var model = _sut.Resolve(id);
        model.Should().NotBeNull();
        model!.Tier.Should().Be(tier);
        model.Id.Should().Be(id);
        model.DisplayName.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Resolve_IsCaseInsensitive()
    {
        _sut.Resolve("CLAUDE-OPUS-4.6").Should().NotBeNull();
        _sut.Resolve("Claude-Opus-4.6").Should().NotBeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-real-model")]
    [InlineData("gpt-99")]
    public void Resolve_UnknownOrEmpty_ReturnsNull(string? id)
    {
        _sut.Resolve(id).Should().BeNull();
    }

    [Fact]
    public void KnownModels_IncludesAtLeastOneModelPerTier()
    {
        var tiers = _sut.KnownModels.Select(m => m.Tier).Distinct().ToHashSet();
        tiers.Should().Contain(new[] { ModelTier.Premium, ModelTier.Standard, ModelTier.Fast });
    }

    [Fact]
    public void KnownModels_AllHavePositiveRates()
    {
        foreach (var m in _sut.KnownModels)
        {
            m.Rates.InputPerMillion.Should().BePositive();
            m.Rates.OutputPerMillion.Should().BePositive();
        }
    }
}
