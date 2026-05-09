using System.Text;
using System.Text.Json;
using CopilotSessionManager.Core.Cli.Adapters.V1;
using CopilotSessionManager.Core.Models;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace CopilotSessionManager.Core.Tests.Cli.Adapters.V1;

public class CopilotCliV1AdapterTests
{
    private static readonly string FixtureRoot = Path.Combine(
        AppContext.BaseDirectory, "fixtures", "copilot-cli", "1.0.43");

    private static CopilotCliV1Adapter CreateAdapter() =>
        new(NullLogger<CopilotCliV1Adapter>.Instance);

    [Fact]
    public void Supports_returns_true_for_v1_x_versions()
    {
        var adapter = CreateAdapter();

        adapter.Supports(new CopilotVersion(1, 0, 0)).Should().BeTrue();
        adapter.Supports(new CopilotVersion(1, 0, 43)).Should().BeTrue();
        adapter.Supports(new CopilotVersion(1, 99, 99)).Should().BeTrue();
    }

    [Fact]
    public void Supports_returns_false_for_other_majors()
    {
        var adapter = CreateAdapter();

        adapter.Supports(new CopilotVersion(0, 9, 0)).Should().BeFalse();
        adapter.Supports(new CopilotVersion(2, 0, 0)).Should().BeFalse();
    }

    [Fact]
    public async Task ReadCopilotVersionAsync_returns_version_from_session_start()
    {
        var adapter = CreateAdapter();
        await using var stream = File.OpenRead(Path.Combine(FixtureRoot, "events.jsonl"));

        var version = await adapter.ReadCopilotVersionAsync(stream);

        version.Should().Be(new CopilotVersion(1, 0, 43));
    }

    [Fact]
    public async Task ReadCopilotVersionAsync_returns_null_when_no_session_start_event()
    {
        var adapter = CreateAdapter();
        var content = "{\"type\":\"assistant.turn_start\",\"id\":\"x\",\"timestamp\":\"2026-05-08T12:00:00.000Z\"}\n";
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));

        var version = await adapter.ReadCopilotVersionAsync(stream);

        version.Should().BeNull();
    }

    [Fact]
    public async Task ParseEventsAsync_skips_malformed_lines_and_blanks()
    {
        var adapter = CreateAdapter();
        await using var stream = File.OpenRead(Path.Combine(FixtureRoot, "events.jsonl"));

        var events = await ToListAsync(adapter.ParseEventsAsync(stream));

        events.Should().HaveCount(6, because: "the fixture has one blank line and one malformed line that are skipped");
        events.Select(e => e.Type).Should().ContainInOrder(
            "session.start",
            "session.resume",
            "assistant.turn_start",
            "assistant.turn_end",
            "permission.requested",
            "permission.completed");
        events[0].Data!.Value.GetProperty("copilotVersion").GetString().Should().Be("1.0.43");
    }

    [Fact]
    public async Task ParseEventsAsync_parses_timestamp_as_utc()
    {
        var adapter = CreateAdapter();
        await using var stream = File.OpenRead(Path.Combine(FixtureRoot, "events.jsonl"));

        var first = (await ToListAsync(adapter.ParseEventsAsync(stream))).First();

        first.Timestamp.Should().Be(new DateTimeOffset(2026, 5, 8, 12, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void ParseWorkspace_parses_all_known_fields()
    {
        var adapter = CreateAdapter();
        var yaml = File.ReadAllText(Path.Combine(FixtureRoot, "workspace.yaml"));

        var ws = adapter.ParseWorkspace(yaml);

        ws.Id.Should().Be("00000000-0000-0000-0000-000000000001");
        ws.Cwd.Should().Be(@"C:\ws\demo");
        ws.GitRoot.Should().Be(@"C:\ws\demo");
        ws.Repository.Should().Be("github/demo");
        ws.HostType.Should().Be("github");
        ws.Branch.Should().Be("main");
        ws.SummaryCount.Should().Be(2);
        ws.CreatedAt.Should().Be(new DateTimeOffset(2026, 5, 8, 12, 0, 0, TimeSpan.Zero));
        ws.UpdatedAt.Should().Be(new DateTimeOffset(2026, 5, 8, 12, 30, 0, TimeSpan.Zero));
        ws.Summary.Should().StartWith("Investigating");
    }

    [Fact]
    public void ParseWorkspace_handles_missing_optional_fields()
    {
        var adapter = CreateAdapter();
        var yaml = "id: abc\n";

        var ws = adapter.ParseWorkspace(yaml);

        ws.Id.Should().Be("abc");
        ws.Repository.Should().BeNull();
        ws.SummaryCount.Should().Be(0);
        ws.CreatedAt.Should().BeNull();
    }

    [Fact]
    public void ParseWorkspace_throws_when_top_level_is_not_a_mapping()
    {
        var adapter = CreateAdapter();
        var act = () => adapter.ParseWorkspace("- a\n- b\n");

        act.Should().Throw<FormatException>();
    }

    private static async Task<List<T>> ToListAsync<T>(IAsyncEnumerable<T> source)
    {
        var list = new List<T>();
        await foreach (var item in source)
        {
            list.Add(item);
        }
        return list;
    }
}
