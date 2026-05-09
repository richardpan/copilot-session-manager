using CopilotSessionManager.Core.Cli;
using CopilotSessionManager.Core.Cli.Adapters.V1;
using CopilotSessionManager.Core.Models;
using CopilotSessionManager.Core.Sessions;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace CopilotSessionManager.Core.Tests.Sessions;

public class SessionStatusEvaluatorTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _stateDir;
    private readonly TestPaths _paths;
    private readonly CopilotCliAdapterRegistry _registry;
    private readonly DateTimeOffset _now = new(2026, 5, 8, 12, 30, 0, TimeSpan.Zero);

    public SessionStatusEvaluatorTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "csm-stat-" + Guid.NewGuid().ToString("N"));
        _stateDir = Path.Combine(_tempRoot, "session-state");
        Directory.CreateDirectory(_stateDir);
        _paths = new TestPaths(Path.Combine(_tempRoot, "session-store.db"), _stateDir);
        _registry = new CopilotCliAdapterRegistry(
            new ICopilotCliAdapter[] { new CopilotCliV1Adapter(NullLogger<CopilotCliV1Adapter>.Instance) },
            NullLogger<CopilotCliAdapterRegistry>.Instance);
    }

    [Fact]
    public async Task Returns_Inactive_when_no_lock_files()
    {
        var evaluator = CreateEvaluator();

        var status = await evaluator.EvaluateAsync(
            "missing", Array.Empty<SessionLockInfo>(), CopilotVersion.Zero, _now);

        status.Should().Be(SessionStatus.Inactive);
    }

    [Fact]
    public async Task Returns_Orphaned_when_all_locks_are_dead()
    {
        var evaluator = CreateEvaluator();
        var locks = new[]
        {
            new SessionLockInfo("/tmp/inuse.1.lock", 1, IsAlive: false),
            new SessionLockInfo("/tmp/inuse.2.lock", 2, IsAlive: false),
        };

        var status = await evaluator.EvaluateAsync("any", locks, CopilotVersion.Zero, _now);

        status.Should().Be(SessionStatus.Orphaned);
    }

    [Fact]
    public async Task Returns_Working_when_live_lock_but_no_events_file()
    {
        var id = Guid.NewGuid().ToString();
        Directory.CreateDirectory(Path.Combine(_stateDir, id));

        var evaluator = CreateEvaluator();
        var locks = new[] { new SessionLockInfo("/tmp/inuse.1.lock", 1, IsAlive: true) };

        var status = await evaluator.EvaluateAsync(id, locks, CopilotVersion.Zero, _now);

        status.Should().Be(SessionStatus.Working);
    }

    [Fact]
    public async Task Returns_Working_when_turn_start_has_no_matching_turn_end()
    {
        var id = Guid.NewGuid().ToString();
        WriteEvents(id, """
            {"type":"session.start","data":{"copilotVersion":"1.0.43"},"id":"e1","timestamp":"2026-05-08T12:00:00.000Z"}
            {"type":"assistant.turn_start","data":{"turnId":"t1"},"id":"e2","timestamp":"2026-05-08T12:25:00.000Z"}
            """);

        var evaluator = CreateEvaluator();
        var locks = AliveLock();

        var status = await evaluator.EvaluateAsync(id, locks, new CopilotVersion(1, 0, 43), _now);

        status.Should().Be(SessionStatus.Working);
    }

    [Fact]
    public async Task Returns_AwaitingApproval_when_permission_requested_has_no_matching_completed()
    {
        var id = Guid.NewGuid().ToString();
        WriteEvents(id, """
            {"type":"session.start","data":{"copilotVersion":"1.0.43"},"id":"e1","timestamp":"2026-05-08T12:00:00.000Z"}
            {"type":"assistant.turn_start","data":{"turnId":"t1"},"id":"e2","timestamp":"2026-05-08T12:01:00.000Z"}
            {"type":"assistant.turn_end","data":{"turnId":"t1"},"id":"e3","timestamp":"2026-05-08T12:25:00.000Z"}
            {"type":"permission.requested","data":{"permissionId":"p1"},"id":"e4","timestamp":"2026-05-08T12:25:30.000Z"}
            """);

        var evaluator = CreateEvaluator();

        var status = await evaluator.EvaluateAsync(id, AliveLock(), new CopilotVersion(1, 0, 43), _now);

        status.Should().Be(SessionStatus.AwaitingApproval);
    }

    [Fact]
    public async Task Permission_completed_clears_AwaitingApproval()
    {
        var id = Guid.NewGuid().ToString();
        WriteEvents(id, """
            {"type":"session.start","data":{"copilotVersion":"1.0.43"},"id":"e1","timestamp":"2026-05-08T12:00:00.000Z"}
            {"type":"permission.requested","data":{"permissionId":"p1"},"id":"e2","timestamp":"2026-05-08T12:25:00.000Z"}
            {"type":"permission.completed","data":{"permissionId":"p1","granted":true},"id":"e3","timestamp":"2026-05-08T12:25:10.000Z"}
            {"type":"assistant.turn_end","data":{"turnId":"t1"},"id":"e4","timestamp":"2026-05-08T12:29:00.000Z"}
            """);

        var evaluator = CreateEvaluator();

        var status = await evaluator.EvaluateAsync(id, AliveLock(), new CopilotVersion(1, 0, 43), _now);

        status.Should().Be(SessionStatus.AwaitingInput);
    }

    [Fact]
    public async Task Returns_AwaitingInput_when_last_event_is_turn_end()
    {
        var id = Guid.NewGuid().ToString();
        WriteEvents(id, """
            {"type":"session.start","data":{"copilotVersion":"1.0.43"},"id":"e1","timestamp":"2026-05-08T12:00:00.000Z"}
            {"type":"assistant.turn_start","data":{"turnId":"t1"},"id":"e2","timestamp":"2026-05-08T12:28:00.000Z"}
            {"type":"assistant.turn_end","data":{"turnId":"t1"},"id":"e3","timestamp":"2026-05-08T12:29:00.000Z"}
            """);

        var evaluator = CreateEvaluator();

        var status = await evaluator.EvaluateAsync(id, AliveLock(), new CopilotVersion(1, 0, 43), _now);

        status.Should().Be(SessionStatus.AwaitingInput);
    }

    [Fact]
    public async Task Returns_Idle_when_no_event_within_threshold()
    {
        var id = Guid.NewGuid().ToString();
        // last event at 12:00; "now" is 12:30; threshold is 5 minutes => Idle.
        WriteEvents(id, """
            {"type":"session.start","data":{"copilotVersion":"1.0.43"},"id":"e1","timestamp":"2026-05-08T12:00:00.000Z"}
            {"type":"assistant.turn_end","data":{"turnId":"t1"},"id":"e2","timestamp":"2026-05-08T12:00:30.000Z"}
            """);

        var evaluator = CreateEvaluator();

        var status = await evaluator.EvaluateAsync(id, AliveLock(), new CopilotVersion(1, 0, 43), _now);

        status.Should().Be(SessionStatus.Idle);
    }

    private SessionStatusEvaluator CreateEvaluator(StatusDetectionOptions? options = null) =>
        new(_paths, _registry, options ?? new StatusDetectionOptions(),
            NullLogger<SessionStatusEvaluator>.Instance);

    private void WriteEvents(string id, string body)
    {
        var dir = Path.Combine(_stateDir, id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "events.jsonl"),
            body.TrimEnd() + Environment.NewLine);
    }

    private static IReadOnlyList<SessionLockInfo> AliveLock() =>
        new[] { new SessionLockInfo("/tmp/inuse.1.lock", 1, IsAlive: true) };

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            try
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
            catch (IOException)
            {
                // Tolerate transient locks on Windows.
            }
        }
        GC.SuppressFinalize(this);
    }

    private sealed class TestPaths : ICopilotPaths
    {
        public TestPaths(string dbPath, string stateDir)
        {
            SessionStoreDatabasePath = dbPath;
            SessionStateDirectory = stateDir;
        }

        public string SessionStoreDatabasePath { get; }
        public string SessionStateDirectory { get; }
    }
}
