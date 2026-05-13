using CopilotSessionManager.Core.Models;
using CopilotSessionManager.Core.Sessions;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace CopilotSessionManager.Core.Tests.Sessions;

public sealed class SubagentScanServiceTests : IDisposable
{
    private const string SessionId = "session-1";
    private readonly string _root;
    private readonly string _stateDir;
    private readonly SubagentScanService _sut;

    public SubagentScanServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "csm-subagents-" + Guid.NewGuid().ToString("N"));
        _stateDir = Path.Combine(_root, "session-state");
        Directory.CreateDirectory(_stateDir);
        _sut = new SubagentScanService(
            new TestPaths(Path.Combine(_root, "session-store.db"), _stateDir),
            NullLogger<SubagentScanService>.Instance);
    }

    [Fact]
    public async Task EmptyFile_ReturnsEmptyList()
    {
        WriteEvents(string.Empty);

        var result = await _sut.ScanAsync(SessionId);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task MissingFile_ReturnsEmptyList()
    {
        var result = await _sut.ScanAsync(SessionId);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task CompletedSubagent_ReturnsSummary()
    {
        WriteEvents(
            ToolStart("call-1", "crash-banner", "task", "2026-05-01T00:00:00Z") +
            Started("call-1", "Crash Banner", "2026-05-01T00:00:01Z") +
            Completed("call-1", 5_200_000, 7, 1234, "claude-sonnet-4.6", "2026-05-01T00:00:03Z"));

        var result = await _sut.ScanAsync(SessionId);

        result.Should().ContainSingle();
        var summary = result[0];
        summary.ToolCallId.Should().Be("call-1");
        summary.Name.Should().Be("crash-banner");
        summary.AgentType.Should().Be("task");
        summary.AgentDisplayName.Should().Be("Crash Banner");
        summary.Model.Should().Be("claude-sonnet-4.6");
        summary.TokensTotal.Should().Be(5_200_000);
        summary.ToolCallsTotal.Should().Be(7);
        summary.Duration.Should().Be(TimeSpan.FromMilliseconds(1234));
        summary.StartedAt.Should().Be(DateTimeOffset.Parse("2026-05-01T00:00:01Z"));
        summary.CompletedAt.Should().Be(DateTimeOffset.Parse("2026-05-01T00:00:03Z"));
        summary.Status.Should().Be(SubagentStatus.Completed);
    }

    [Fact]
    public async Task RunningSubagent_HasRunningStatusAndZeroTotals()
    {
        WriteEvents(
            ToolStart("call-1", "runner", "explore", "2026-05-01T00:00:00Z") +
            Started("call-1", "Runner", "2026-05-01T00:00:02Z"));

        var result = await _sut.ScanAsync(SessionId);

        result.Should().ContainSingle();
        result[0].Status.Should().Be(SubagentStatus.Running);
        result[0].TokensTotal.Should().Be(0);
        result[0].ToolCallsTotal.Should().Be(0);
        result[0].Duration.Should().BeNull();
        result[0].CompletedAt.Should().BeNull();
    }

    [Fact]
    public async Task MultipleSubagentsInterleaved_ReturnsAllSortedByStartedAt()
    {
        WriteEvents(
            ToolStart("call-b", "second", "task", "2026-05-01T00:00:00Z") +
            ToolStart("call-a", "first", "task", "2026-05-01T00:00:00Z") +
            Started("call-b", "Second", "2026-05-01T00:00:05Z") +
            Started("call-a", "First", "2026-05-01T00:00:01Z") +
            Completed("call-b", 20, 2, 200, "m2", "2026-05-01T00:00:08Z") +
            Completed("call-a", 10, 1, 100, "m1", "2026-05-01T00:00:03Z"));

        var result = await _sut.ScanAsync(SessionId);

        result.Select(s => s.ToolCallId).Should().Equal("call-a", "call-b");
    }

    [Fact]
    public async Task ToolCallWithoutStarted_IsSkipped()
    {
        WriteEvents(
            ToolStart("call-1", "missing-start", "task", "2026-05-01T00:00:00Z") +
            Completed("call-1", 10, 1, 100, "m1", "2026-05-01T00:00:03Z"));

        var result = await _sut.ScanAsync(SessionId);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task CompletedWithoutMatchingStarted_IsSkipped()
    {
        WriteEvents(Completed("call-1", 10, 1, 100, "m1", "2026-05-01T00:00:03Z"));

        var result = await _sut.ScanAsync(SessionId);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task MalformedJsonLine_IsSkipped()
    {
        WriteEvents(
            "not json\n" +
            ToolStart("call-1", "valid", "task", "2026-05-01T00:00:00Z") +
            Started("call-1", "Valid", "2026-05-01T00:00:01Z"));

        var result = await _sut.ScanAsync(SessionId);

        result.Should().ContainSingle().Which.Name.Should().Be("valid");
    }

    [Fact]
    public async Task MissingAgentType_DefaultsToUnknown()
    {
        WriteEvents(
            Event("tool.execution_start", "2026-05-01T00:00:00Z", "\"toolName\":\"task\",\"toolCallId\":\"call-1\",\"arguments\":{\"name\":\"no-type\"}") +
            Started("call-1", "No Type", "2026-05-01T00:00:01Z"));

        var result = await _sut.ScanAsync(SessionId);

        result.Should().ContainSingle().Which.AgentType.Should().Be("unknown");
    }

    [Fact]
    public async Task MissingName_DefaultsToTask()
    {
        WriteEvents(
            Event("tool.execution_start", "2026-05-01T00:00:00Z", "\"toolName\":\"task\",\"toolCallId\":\"call-1\",\"arguments\":{\"agent_type\":\"explore\"}") +
            Started("call-1", "No Name", "2026-05-01T00:00:01Z"));

        var result = await _sut.ScanAsync(SessionId);

        result.Should().ContainSingle().Which.Name.Should().Be("task");
    }

    [Fact]
    public async Task NonTaskToolExecutionStart_IsIgnored()
    {
        WriteEvents(
            Event("tool.execution_start", "2026-05-01T00:00:00Z", "\"toolName\":\"powershell\",\"toolCallId\":\"call-1\"") +
            Started("call-1", "Shell", "2026-05-01T00:00:01Z"));

        var result = await _sut.ScanAsync(SessionId);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task StartedWithoutToolExecutionStart_IsSkipped()
    {
        WriteEvents(Started("call-1", "Orphan", "2026-05-01T00:00:01Z"));

        var result = await _sut.ScanAsync(SessionId);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task CompletedWithCancelledStatus_ReturnsCancelledSummary()
    {
        WriteEvents(
            ToolStart("call-1", "cancelled", "task", "2026-05-01T00:00:00Z") +
            Started("call-1", "Cancelled", "2026-05-01T00:00:01Z") +
            Event("subagent.completed", "2026-05-01T00:00:03Z", "\"agentId\":\"call-1\",\"totalTokens\":10,\"totalToolCalls\":1,\"durationMs\":100,\"model\":\"m1\",\"status\":\"cancelled\""));

        var result = await _sut.ScanAsync(SessionId);

        result.Should().ContainSingle().Which.Status.Should().Be(SubagentStatus.Cancelled);
    }

    [Fact]
    public async Task CancelledToken_ThrowsOperationCanceledException()
    {
        WriteEvents(string.Empty);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => _sut.ScanAsync(SessionId, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    public void Dispose()
    {
        if (!Directory.Exists(_root))
        {
            return;
        }

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void WriteEvents(string jsonl)
    {
        var dir = Path.Combine(_stateDir, SessionId);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "events.jsonl"), jsonl);
    }

    private static string ToolStart(string id, string name, string agentType, string timestamp) =>
        Event("tool.execution_start", timestamp, $"\"toolName\":\"task\",\"toolCallId\":\"{id}\",\"arguments\":{{\"name\":\"{name}\",\"agent_type\":\"{agentType}\",\"description\":\"desc\"}}");

    private static string Started(string id, string displayName, string timestamp) =>
        Event("subagent.started", timestamp, $"\"agentId\":\"{id}\",\"agentName\":\"{displayName}\",\"agentDisplayName\":\"{displayName}\",\"agentDescription\":\"desc\"");

    private static string Completed(string id, long tokens, int tools, int durationMs, string model, string timestamp) =>
        Event("subagent.completed", timestamp, $"\"agentId\":\"{id}\",\"totalTokens\":{tokens},\"totalToolCalls\":{tools},\"durationMs\":{durationMs},\"model\":\"{model}\"");

    private static string Event(string type, string timestamp, string data) =>
        $"{{\"id\":\"{Guid.NewGuid():N}\",\"type\":\"{type}\",\"timestamp\":\"{timestamp}\",\"data\":{{{data}}}}}\n";

    private sealed record TestPaths(string SessionStoreDatabasePath, string SessionStateDirectory) : ICopilotPaths;
}
