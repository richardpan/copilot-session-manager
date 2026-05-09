using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows.Media;
using CopilotSessionManager.Core.Cli;
using CopilotSessionManager.Core.Cost;
using CopilotSessionManager.Core.Models;
using CopilotSessionManager.ViewModels;
using FluentAssertions;
using Xunit;

namespace CopilotSessionManager.Tests.ViewModels;

public class SessionCardViewModelModelInfoTests
{
    private static readonly DateTimeOffset Now = new(2026, 5, 8, 12, 0, 0, TimeSpan.Zero);

    private static readonly IModelCatalog Catalog = new EmbeddedModelCatalog();
    private static readonly IModelCostCalculator Calculator = new ModelCostCalculator(Catalog);

    private static Session BuildSession(SessionModelInfo? info) => new(
        Id: "abc",
        Cwd: @"C:\ws\repo",
        Repository: "owner/repo",
        Branch: "main",
        Summary: "test session",
        HostType: "cli",
        CreatedAt: Now.AddMinutes(-5),
        UpdatedAt: Now.AddMinutes(-1),
        TurnCount: 1,
        Status: SessionStatus.Idle,
        CopilotVersion: CopilotVersion.Zero,
        Locks: Array.Empty<SessionLockInfo>(),
        ModelInfo: info);

    [Fact]
    public void NullModelInfo_RendersUnknownAndDash()
    {
        var card = new SessionCardViewModel(
            BuildSession(null), SessionType.Exploratory, TimeProvider.System, Catalog, Calculator);

        card.ModelDisplay.Should().Be("Model unknown");
        card.ModelTier.Should().Be(ModelTier.Unknown);
        card.CostDisplay.Should().Be("—");
        card.ModelTooltip.Should().Contain("Model unknown");
    }

    [Fact]
    public void KnownModelWithoutShutdown_ShowsNameAndDash()
    {
        var info = new SessionModelInfo("claude-opus-4.6", IsFromShutdown: false,
            new Dictionary<string, ModelUsage>(StringComparer.Ordinal));
        var card = new SessionCardViewModel(
            BuildSession(info), SessionType.Exploratory, TimeProvider.System, Catalog, Calculator);

        card.ModelDisplay.Should().Be("Opus 4.6");
        card.ModelTier.Should().Be(ModelTier.Premium);
        card.CostDisplay.Should().Be("—", because: "no usage data outside of shutdown");
        card.ModelTooltip.Should().Contain("Tier: Premium").And.Contain("only available after");
    }

    [Fact]
    public void ShutdownWithKnownUsage_ShowsCurrencyFormattedCost()
    {
        var usage = new ModelUsage(1_000_000, 100_000, 500_000, 0, 0, 10);
        var info = new SessionModelInfo("claude-opus-4.6", true,
            new Dictionary<string, ModelUsage>(StringComparer.Ordinal) { ["claude-opus-4.6"] = usage });
        var card = new SessionCardViewModel(
            BuildSession(info), SessionType.Exploratory, TimeProvider.System, Catalog, Calculator);

        // 23.25 USD — formatted as currency, en-US.
        card.CostDisplay.Should().Be(23.25m.ToString("C2", CultureInfo.GetCultureInfo("en-US")));
    }

    [Fact]
    public void UnknownModel_PrependsTilde()
    {
        var info = new SessionModelInfo("mystery-9000", true,
            new Dictionary<string, ModelUsage>(StringComparer.Ordinal)
            {
                ["claude-haiku-4.5"] = new(1_000_000, 0, 0, 0, 0, 1),  // contributes $1.00
                ["mystery-9000"] = new(1_000_000, 0, 0, 0, 0, 1),
            });
        var card = new SessionCardViewModel(
            BuildSession(info), SessionType.Exploratory, TimeProvider.System, Catalog, Calculator);

        card.CostDisplay.Should().StartWith("~");
        card.ModelTier.Should().Be(ModelTier.Unknown);
        card.ModelDisplay.Should().Be("mystery-9000", because: "unknown ids fall through to the raw id");
    }

    [Theory]
    [InlineData(ModelTier.Premium, 0xF3, 0x8B, 0xA8)]
    [InlineData(ModelTier.Standard, 0x89, 0xB4, 0xFA)]
    [InlineData(ModelTier.Fast, 0xA6, 0xE3, 0xA1)]
    [InlineData(ModelTier.Unknown, 0x7F, 0x84, 0x9C)]
    public void ModelTierBrush_MatchesExpectedColor(ModelTier tier, byte r, byte g, byte b)
    {
        var modelId = tier switch
        {
            ModelTier.Premium => "claude-opus-4.6",
            ModelTier.Standard => "claude-sonnet-4.6",
            ModelTier.Fast => "claude-haiku-4.5",
            _ => "mystery",
        };
        var info = new SessionModelInfo(modelId, false,
            new Dictionary<string, ModelUsage>(StringComparer.Ordinal));
        var card = new SessionCardViewModel(
            BuildSession(info), SessionType.Exploratory, TimeProvider.System, Catalog, Calculator);

        var brush = (SolidColorBrush)card.ModelTierBrush;
        brush.Color.R.Should().Be(r);
        brush.Color.G.Should().Be(g);
        brush.Color.B.Should().Be(b);
    }

    [Fact]
    public void UpdateFrom_NewModel_RaisesModelChangeNotifications()
    {
        var initial = new SessionModelInfo("claude-haiku-4.5", false,
            new Dictionary<string, ModelUsage>(StringComparer.Ordinal));
        var card = new SessionCardViewModel(
            BuildSession(initial), SessionType.Exploratory, TimeProvider.System, Catalog, Calculator);

        var changed = new HashSet<string>();
        card.PropertyChanged += (_, e) => changed.Add(e.PropertyName!);

        var next = BuildSession(new SessionModelInfo("claude-opus-4.6", true,
            new Dictionary<string, ModelUsage>(StringComparer.Ordinal)
            {
                ["claude-opus-4.6"] = new(1_000_000, 0, 0, 0, 0, 1),
            }));
        card.UpdateFrom(next);

        changed.Should().Contain(new[]
        {
            nameof(SessionCardViewModel.ModelDisplay),
            nameof(SessionCardViewModel.ModelTier),
            nameof(SessionCardViewModel.ModelTierBrush),
            nameof(SessionCardViewModel.CostDisplay),
            nameof(SessionCardViewModel.ModelTooltip),
        });
        card.ModelDisplay.Should().Be("Opus 4.6");
    }
}
