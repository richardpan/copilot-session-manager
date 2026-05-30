using System.Text;
using CopilotSessionManager.Core.Cli;
using CopilotSessionManager.Core.Cli.Adapters.V1;
using CopilotSessionManager.Core.Models;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CopilotSessionManager.Core.Tests.Cli.Adapters.V1;

/// <summary>
/// Tests the model-info reader through its public surface
/// (<see cref="ICopilotCliAdapter.ReadSessionModelInfoAsync"/>).
/// </summary>
public class SessionModelInfoReaderTests
{
    private readonly ICopilotCliAdapter _sut = new CopilotCliV1Adapter(
        NullLogger<CopilotCliV1Adapter>.Instance);

    private static Stream StreamFor(params string[] lines)
    {
        var bytes = Encoding.UTF8.GetBytes(string.Join('\n', lines));
        return new MemoryStream(bytes);
    }

    [Fact]
    public async Task EmptyStream_ReturnsEmpty()
    {
        await using var s = StreamFor();
        var info = await _sut.ReadSessionModelInfoAsync(s);
        info.Should().BeSameAs(SessionModelInfo.Empty);
    }

    [Fact]
    public async Task SessionStart_OnlySource_ReturnsSelectedModel()
    {
        await using var s = StreamFor(
            "{\"type\":\"session.start\",\"id\":\"a\",\"data\":{\"selectedModel\":\"claude-opus-4.6\"}}");
        var info = await _sut.ReadSessionModelInfoAsync(s);

        info.CurrentModelId.Should().Be("claude-opus-4.6");
        info.IsFromShutdown.Should().BeFalse();
        info.UsageByModel.Should().BeEmpty();
    }

    [Fact]
    public async Task ToolExecution_OverridesSessionStart_ForActiveSession()
    {
        await using var s = StreamFor(
            "{\"type\":\"session.start\",\"id\":\"a\",\"data\":{\"selectedModel\":\"claude-opus-4.6\"}}",
            "{\"type\":\"tool.execution_complete\",\"id\":\"b\",\"data\":{\"model\":\"claude-sonnet-4.6\"}}",
            "{\"type\":\"tool.execution_complete\",\"id\":\"c\",\"data\":{\"model\":\"claude-haiku-4.5\"}}");
        var info = await _sut.ReadSessionModelInfoAsync(s);

        info.CurrentModelId.Should().Be("claude-haiku-4.5", because: "the most recent tool execution wins");
        info.IsFromShutdown.Should().BeFalse();
        info.UsageByModel.Should().BeEmpty();
    }

    [Fact]
    public async Task Shutdown_BeatsToolExecution_AndCarriesUsage()
    {
        await using var s = StreamFor(
            "{\"type\":\"session.start\",\"id\":\"a\",\"data\":{\"selectedModel\":\"claude-opus-4.6\"}}",
            "{\"type\":\"tool.execution_complete\",\"id\":\"b\",\"data\":{\"model\":\"claude-haiku-4.5\"}}",
            "{\"type\":\"session.shutdown\",\"id\":\"c\",\"data\":{\"currentModel\":\"claude-opus-4.6\"," +
                "\"modelMetrics\":{\"claude-opus-4.6\":{" +
                    "\"requests\":{\"count\":16,\"cost\":18}," +
                    "\"usage\":{\"inputTokens\":1225291,\"outputTokens\":5139,\"cacheReadTokens\":1056012,\"cacheWriteTokens\":0,\"reasoningTokens\":0}" +
                "}}}}");
        var info = await _sut.ReadSessionModelInfoAsync(s);

        info.IsFromShutdown.Should().BeTrue();
        info.CurrentModelId.Should().Be("claude-opus-4.6");
        info.UsageByModel.Should().ContainKey("claude-opus-4.6");
        var u = info.UsageByModel["claude-opus-4.6"];
        u.InputTokens.Should().Be(1225291);
        u.OutputTokens.Should().Be(5139);
        u.CacheReadTokens.Should().Be(1056012);
        u.CacheWriteTokens.Should().Be(0);
        u.RequestCount.Should().Be(16);
    }

    [Fact]
    public async Task Shutdown_WithoutCurrentModel_FallsBackToMostRequestedModel()
    {
        await using var s = StreamFor(
            "{\"type\":\"session.shutdown\",\"id\":\"c\",\"data\":{" +
                "\"modelMetrics\":{" +
                    "\"a\":{\"requests\":{\"count\":3},\"usage\":{\"inputTokens\":100}}," +
                    "\"b\":{\"requests\":{\"count\":17},\"usage\":{\"inputTokens\":200}}" +
                "}}}");
        var info = await _sut.ReadSessionModelInfoAsync(s);

        info.IsFromShutdown.Should().BeTrue();
        info.CurrentModelId.Should().Be("b");
        info.UsageByModel.Should().HaveCount(2);
    }

    [Fact]
    public async Task LaterShutdown_Wins_OverEarlierShutdown()
    {
        await using var s = StreamFor(
            "{\"type\":\"session.shutdown\",\"id\":\"a\",\"data\":{\"currentModel\":\"x\",\"modelMetrics\":{}}}",
            "{\"type\":\"session.shutdown\",\"id\":\"b\",\"data\":{\"currentModel\":\"y\",\"modelMetrics\":{}}}");
        var info = await _sut.ReadSessionModelInfoAsync(s);

        info.CurrentModelId.Should().Be("y");
    }

    [Fact]
    public async Task MalformedLines_AreSkipped()
    {
        await using var s = StreamFor(
            "garbage{not valid json",
            "{\"type\":\"session.start\",\"id\":\"a\",\"data\":{\"selectedModel\":\"claude-haiku-4.5\"}}",
            "another garbage line");
        var info = await _sut.ReadSessionModelInfoAsync(s);

        info.CurrentModelId.Should().Be("claude-haiku-4.5");
    }

    [Fact]
    public async Task ToolExecution_BlankModelString_IsIgnored()
    {
        await using var s = StreamFor(
            "{\"type\":\"session.start\",\"id\":\"a\",\"data\":{\"selectedModel\":\"claude-opus-4.6\"}}",
            "{\"type\":\"tool.execution_complete\",\"id\":\"b\",\"data\":{\"model\":\"   \"}}");
        var info = await _sut.ReadSessionModelInfoAsync(s);

        info.CurrentModelId.Should().Be("claude-opus-4.6");
    }

    [Fact]
    public async Task AssistantMessages_AccumulateLiveOutputTokensPerModel()
    {
        await using var s = StreamFor(
            "{\"type\":\"session.start\",\"id\":\"a\",\"data\":{\"selectedModel\":\"claude-opus-4.7\"}}",
            "{\"type\":\"assistant.message\",\"id\":\"b\",\"data\":{\"model\":\"claude-opus-4.7\",\"outputTokens\":120}}",
            "{\"type\":\"assistant.message\",\"id\":\"c\",\"data\":{\"model\":\"claude-opus-4.7\",\"outputTokens\":80}}",
            "{\"type\":\"assistant.message\",\"id\":\"d\",\"data\":{\"model\":\"gpt-5.4\",\"outputTokens\":50}}");
        var info = await _sut.ReadSessionModelInfoAsync(s);

        info.IsFromShutdown.Should().BeFalse();
        info.CurrentModelId.Should().Be("gpt-5.4");
        info.UsageByModel.Should().HaveCount(2);
        info.UsageByModel["claude-opus-4.7"].OutputTokens.Should().Be(200);
        info.UsageByModel["claude-opus-4.7"].RequestCount.Should().Be(2);
        info.UsageByModel["gpt-5.4"].OutputTokens.Should().Be(50);
        info.UsageByModel["gpt-5.4"].RequestCount.Should().Be(1);
    }

    [Fact]
    public async Task CompactionEvents_AddInputAndCacheTokensToLiveTally()
    {
        await using var s = StreamFor(
            "{\"type\":\"assistant.message\",\"id\":\"a\",\"data\":{\"model\":\"claude-opus-4.7\",\"outputTokens\":300}}",
            "{\"type\":\"session.compaction_complete\",\"id\":\"b\",\"data\":{\"compactionTokensUsed\":{\"model\":\"claude-opus-4.7\",\"inputTokens\":1000,\"outputTokens\":150,\"cacheReadTokens\":2000,\"cacheWriteTokens\":500}}}");
        var info = await _sut.ReadSessionModelInfoAsync(s);

        info.IsFromShutdown.Should().BeFalse();
        var usage = info.UsageByModel["claude-opus-4.7"];
        usage.InputTokens.Should().Be(1000);
        usage.OutputTokens.Should().Be(450); // 300 streaming + 150 compaction
        usage.CacheReadTokens.Should().Be(2000);
        usage.CacheWriteTokens.Should().Be(500);
    }

    [Fact]
    public async Task SessionShutdown_StillTakesPrecedenceOverLiveTally()
    {
        // If a shutdown event is present, its authoritative metrics win over
        // anything we accumulated from streaming assistant.message events.
        await using var s = StreamFor(
            "{\"type\":\"assistant.message\",\"id\":\"a\",\"data\":{\"model\":\"claude-opus-4.7\",\"outputTokens\":999999}}",
            "{\"type\":\"session.shutdown\",\"id\":\"b\",\"data\":{\"currentModel\":\"claude-opus-4.7\",\"modelMetrics\":{\"claude-opus-4.7\":{\"requests\":{\"count\":7},\"usage\":{\"inputTokens\":11,\"outputTokens\":22,\"cacheReadTokens\":33,\"cacheWriteTokens\":44}}}}}");
        var info = await _sut.ReadSessionModelInfoAsync(s);

        info.IsFromShutdown.Should().BeTrue();
        var usage = info.UsageByModel["claude-opus-4.7"];
        usage.OutputTokens.Should().Be(22);
        usage.InputTokens.Should().Be(11);
        usage.RequestCount.Should().Be(7);
    }
}
