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
}
