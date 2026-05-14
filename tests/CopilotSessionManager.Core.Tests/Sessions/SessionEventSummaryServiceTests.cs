using CopilotSessionManager.Core.Models;
using CopilotSessionManager.Core.Sessions;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace CopilotSessionManager.Core.Tests.Sessions;

public sealed class SessionEventSummaryServiceTests : IDisposable
{
    private const string SessionId = "session-1";
    private readonly string _root;
    private readonly string _stateDir;
    private readonly SessionEventSummaryService _sut;

    public SessionEventSummaryServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "csm-evtsum-" + Guid.NewGuid().ToString("N"));
        _stateDir = Path.Combine(_root, "session-state");
        Directory.CreateDirectory(_stateDir);
        _sut = new SessionEventSummaryService(
            new TestPaths(Path.Combine(_root, "session-store.db"), _stateDir),
            NullLogger<SessionEventSummaryService>.Instance);
    }

    [Fact]
    public async Task MissingFile_ReturnsEmpty()
    {
        var result = await _sut.ScanAsync(SessionId);

        result.Should().BeSameAs(SessionEventSummary.Empty);
    }

    [Fact]
    public async Task EmptyFile_ReturnsEmptySummary()
    {
        WriteEvents(string.Empty);

        var result = await _sut.ScanAsync(SessionId);

        result.RecentPrompts.Should().BeEmpty();
        result.TopTools.Should().BeEmpty();
        result.LongestIdleGap.Should().BeNull();
        result.TotalActiveSpan.Should().BeNull();
        result.TotalEvents.Should().Be(0);
    }

    [Fact]
    public async Task UserMessages_ReturnedNewestFirst_AndCappedAtFive()
    {
        var jsonl = string.Concat(
            UserMessage("first", "2026-05-01T00:00:00Z"),
            UserMessage("second", "2026-05-01T00:00:01Z"),
            UserMessage("third", "2026-05-01T00:00:02Z"),
            UserMessage("fourth", "2026-05-01T00:00:03Z"),
            UserMessage("fifth", "2026-05-01T00:00:04Z"),
            UserMessage("sixth", "2026-05-01T00:00:05Z"));
        WriteEvents(jsonl);

        var result = await _sut.ScanAsync(SessionId);

        result.RecentPrompts
            .Select(p => p.Body)
            .Should().Equal("sixth", "fifth", "fourth", "third", "second");
    }

    [Fact]
    public async Task UserMessage_TruncatesLongBodies_WithEllipsis()
    {
        var longBody = new string('a', SessionEventSummary.MaxPromptBodyChars + 50);
        WriteEvents(UserMessage(longBody, "2026-05-01T00:00:00Z"));

        var result = await _sut.ScanAsync(SessionId);

        var prompt = result.RecentPrompts.Single();
        prompt.Body.Length.Should().Be(SessionEventSummary.MaxPromptBodyChars + 1);
        prompt.Body.Should().EndWith("…");
    }

    [Fact]
    public async Task UserMessage_CollapsesNewlinesIntoSpaces()
    {
        WriteEvents(UserMessage("line1\nline2\r\nline3", "2026-05-01T00:00:00Z"));

        var result = await _sut.ScanAsync(SessionId);

        result.RecentPrompts.Single().Body.Should().Be("line1 line2 line3");
    }

    [Fact]
    public async Task EmptyOrWhitespaceUserMessage_IsIgnored()
    {
        WriteEvents(
            UserMessage("", "2026-05-01T00:00:00Z") +
            UserMessage("   ", "2026-05-01T00:00:01Z") +
            UserMessage("real", "2026-05-01T00:00:02Z"));

        var result = await _sut.ScanAsync(SessionId);

        result.RecentPrompts.Should().ContainSingle().Which.Body.Should().Be("real");
    }

    [Fact]
    public async Task ToolUsage_HistogramIsDescendingByCountThenName_AndCappedAtTen()
    {
        var sb = new System.Text.StringBuilder();
        // grep: 5, view: 3, edit: 2, plus 9 single-use tools to push past the cap
        for (var i = 0; i < 5; i++)
            sb.Append(ToolStart("grep", "2026-05-01T00:00:00Z"));
        for (var i = 0; i < 3; i++)
            sb.Append(ToolStart("view", "2026-05-01T00:00:00Z"));
        for (var i = 0; i < 2; i++)
            sb.Append(ToolStart("edit", "2026-05-01T00:00:00Z"));
        for (var i = 0; i < 9; i++)
            sb.Append(ToolStart("single_" + i, "2026-05-01T00:00:00Z"));
        WriteEvents(sb.ToString());

        var result = await _sut.ScanAsync(SessionId);

        result.TopTools.Should().HaveCount(SessionEventSummary.MaxTopTools);
        result.TopTools[0].Should().BeEquivalentTo(new ToolUsageCount("grep", 5));
        result.TopTools[1].Should().BeEquivalentTo(new ToolUsageCount("view", 3));
        result.TopTools[2].Should().BeEquivalentTo(new ToolUsageCount("edit", 2));
        // remaining 7 are tied at 1; alphabetic by name
        result.TopTools.Skip(3).Select(t => t.ToolName)
            .Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task ToolUsage_IgnoresEntriesWithoutToolName()
    {
        WriteEvents(
            Event("tool.execution_start", "2026-05-01T00:00:00Z", "\"toolCallId\":\"x\"") +
            ToolStart("grep", "2026-05-01T00:00:01Z"));

        var result = await _sut.ScanAsync(SessionId);

        result.TopTools.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new ToolUsageCount("grep", 1));
    }

    [Fact]
    public async Task ActivitySpan_AndLongestGap_ComputedFromEveryEvent()
    {
        // Three events 10 minutes / 1 hour apart; longest gap = 1h, span = 1h10m.
        WriteEvents(
            UserMessage("a", "2026-05-01T00:00:00Z") +
            UserMessage("b", "2026-05-01T00:10:00Z") +
            UserMessage("c", "2026-05-01T01:10:00Z"));

        var result = await _sut.ScanAsync(SessionId);

        result.TotalActiveSpan.Should().Be(TimeSpan.FromMinutes(70));
        result.LongestIdleGap.Should().Be(TimeSpan.FromHours(1));
        result.TotalEvents.Should().Be(3);
    }

    [Fact]
    public async Task SingleEvent_HasNullSpanAndNullGap()
    {
        WriteEvents(UserMessage("solo", "2026-05-01T00:00:00Z"));

        var result = await _sut.ScanAsync(SessionId);

        result.TotalActiveSpan.Should().BeNull();
        result.LongestIdleGap.Should().BeNull();
    }

    [Fact]
    public async Task MalformedJsonLine_IsSkipped()
    {
        WriteEvents(
            "not json\n" +
            UserMessage("survivor", "2026-05-01T00:00:00Z"));

        var result = await _sut.ScanAsync(SessionId);

        result.RecentPrompts.Should().ContainSingle().Which.Body.Should().Be("survivor");
        result.TotalEvents.Should().Be(1);
    }

    [Fact]
    public async Task RealUserMessageShape_IsParsed()
    {
        // Pinned to the real Copilot CLI shape: data.content carries the user
        // body; data.transformedContent carries the wrapped form with system
        // reminders. We must use `content`, not `transformedContent`.
        const string line =
            "{\"type\":\"user.message\",\"data\":{\"content\":\"Add session labels\",\"transformedContent\":\"<reminder>foo</reminder>\\n\\nAdd session labels\",\"attachments\":[],\"interactionId\":\"ix-1\"},\"id\":\"e1\",\"timestamp\":\"2026-05-09T16:43:11.124Z\",\"parentId\":\"p1\"}\n";
        WriteEvents(line);

        var result = await _sut.ScanAsync(SessionId);

        result.RecentPrompts.Single().Body.Should().Be("Add session labels");
    }

    [Fact]
    public async Task CancelledToken_ThrowsOperationCanceledException()
    {
        WriteEvents(UserMessage("hi", "2026-05-01T00:00:00Z"));
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

    private static string UserMessage(string body, string timestamp)
    {
        var escaped = body
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);
        return Event("user.message", timestamp, $"\"content\":\"{escaped}\"");
    }

    private static string ToolStart(string toolName, string timestamp) =>
        Event("tool.execution_start", timestamp, $"\"toolName\":\"{toolName}\",\"toolCallId\":\"{Guid.NewGuid():N}\"");

    private static string Event(string type, string timestamp, string data) =>
        $"{{\"id\":\"{Guid.NewGuid():N}\",\"type\":\"{type}\",\"timestamp\":\"{timestamp}\",\"data\":{{{data}}}}}\n";

    private sealed record TestPaths(string SessionStoreDatabasePath, string SessionStateDirectory) : ICopilotPaths;
}
