using CopilotSessionManager.Core.Cli;
using CopilotSessionManager.Core.Cli.Adapters.V1;
using CopilotSessionManager.Core.Models;
using CopilotSessionManager.Core.Sessions;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace CopilotSessionManager.Core.Tests.Sessions;

public class SessionDiscoveryServiceTests : IAsyncDisposable, IDisposable
{
    private readonly string _tempRoot;
    private readonly string _stateDir;
    private readonly string _dbPath;
    private SessionDiscoveryService? _service;

    public SessionDiscoveryServiceTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "csm-disc-" + Guid.NewGuid().ToString("N"));
        _stateDir = Path.Combine(_tempRoot, "session-state");
        _dbPath = Path.Combine(_tempRoot, "session-store.db");
        Directory.CreateDirectory(_stateDir);
    }

    [Fact]
    public async Task ScanAsync_combines_db_records_with_workspace_yaml()
    {
        var id = "00000000-0000-0000-0000-000000000001";
        WriteSessionFiles(id, copilotVersion: "1.0.43", repository: "github/demo", branch: "main");

        var service = CreateService(records: new[]
        {
            new SessionStoreRecord(id, @"C:\ws\demo", "github/demo", "main", "DB summary",
                "github", DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow, TurnCount: 7),
        });

        var sessions = await service.ScanAsync();

        sessions.Should().ContainSingle();
        var s = sessions[0];
        s.Id.Should().Be(id);
        s.TurnCount.Should().Be(7);
        s.Repository.Should().Be("github/demo");
        s.Branch.Should().Be("main");
        s.Summary.Should().Be("DB summary", because: "DB columns take precedence over workspace.yaml fallback");
        s.CopilotVersion.Should().Be(new CopilotVersion(1, 0, 43));
        s.Status.Should().Be(SessionStatus.Inactive);
    }

    [Fact]
    public async Task ScanAsync_includes_state_directory_only_sessions()
    {
        var id = "00000000-0000-0000-0000-000000000002";
        WriteSessionFiles(id, copilotVersion: "1.0.43", repository: "github/orphan", branch: "feature");

        var service = CreateService(records: Array.Empty<SessionStoreRecord>());

        var sessions = await service.ScanAsync();

        sessions.Should().ContainSingle();
        sessions[0].Id.Should().Be(id);
        sessions[0].TurnCount.Should().Be(0);
        sessions[0].Repository.Should().Be("github/orphan");
    }

    [Fact]
    public async Task ScanAsync_includes_db_only_sessions()
    {
        var id = "00000000-0000-0000-0000-000000000003";
        var service = CreateService(records: new[]
        {
            new SessionStoreRecord(id, @"C:\ws\dbonly", "github/dbonly", "main", "DB only summary",
                "github", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, TurnCount: 1),
        });

        var sessions = await service.ScanAsync();

        sessions.Should().ContainSingle();
        sessions[0].Repository.Should().Be("github/dbonly");
        sessions[0].CopilotVersion.Should().Be(CopilotVersion.Zero);
    }

    [Fact]
    public async Task ScanAsync_orders_sessions_by_updated_at_descending()
    {
        var older = "00000000-0000-0000-0000-000000000010";
        var newer = "00000000-0000-0000-0000-000000000020";
        var service = CreateService(records: new[]
        {
            new SessionStoreRecord(older, null, null, null, null, null,
                new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero), 0),
            new SessionStoreRecord(newer, null, null, null, null, null,
                new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 5, 2, 0, 0, 0, TimeSpan.Zero), 0),
        });

        var sessions = await service.ScanAsync();

        sessions.Select(s => s.Id).Should().ContainInOrder(newer, older);
    }

    [Fact]
    public async Task StartWatchingAsync_raises_SessionsChanged_when_a_new_state_directory_appears()
    {
        var service = CreateService(records: Array.Empty<SessionStoreRecord>());
        var tcs = new TaskCompletionSource<IReadOnlyList<Session>>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.SessionsChanged += (_, e) => tcs.TrySetResult(e.Sessions);

        await service.StartWatchingAsync();

        var newId = "00000000-0000-0000-0000-000000000099";
        WriteSessionFiles(newId, copilotVersion: "1.0.43", repository: "github/new", branch: "main");

        var completed = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        completed.Should().Be(tcs.Task, because: "the watcher should detect the new directory and rescan");

        var sessions = await tcs.Task;
        sessions.Should().Contain(s => s.Id == newId);
    }

    private SessionDiscoveryService CreateService(IReadOnlyList<SessionStoreRecord> records)
    {
        var paths = new TestPaths(_dbPath, _stateDir);
        var store = new FakeStore(records);
        var registry = new CopilotCliAdapterRegistry(
            new ICopilotCliAdapter[] { new CopilotCliV1Adapter(NullLogger<CopilotCliV1Adapter>.Instance) },
            NullLogger<CopilotCliAdapterRegistry>.Instance);
        _service = new SessionDiscoveryService(
            store,
            registry,
            paths,
            NullLogger<SessionDiscoveryService>.Instance);
        return _service;
    }

    private void WriteSessionFiles(string id, string copilotVersion, string repository, string branch)
    {
        var sessionDir = Path.Combine(_stateDir, id);
        Directory.CreateDirectory(sessionDir);

        var workspace = $"""
            id: {id}
            cwd: C:\ws\demo
            repository: {repository}
            host_type: github
            branch: {branch}
            summary: workspace summary
            created_at: 2026-05-08T12:00:00.000Z
            updated_at: 2026-05-08T12:30:00.000Z
            """;
        File.WriteAllText(Path.Combine(sessionDir, "workspace.yaml"), workspace);

        var startEvent = $$"""
            {"data":{"copilotVersion":"{{copilotVersion}}","sessionId":"{{id}}","startTime":"2026-05-08T12:00:00.000Z","version":1},"id":"e1","parentId":null,"timestamp":"2026-05-08T12:00:00.000Z","type":"session.start"}
            """;
        File.WriteAllText(Path.Combine(sessionDir, "events.jsonl"), startEvent + Environment.NewLine);
    }

    public async ValueTask DisposeAsync()
    {
        if (_service is not null)
        {
            await _service.StopWatchingAsync();
            await _service.DisposeAsync();
        }
        TryCleanup();
        GC.SuppressFinalize(this);
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private void TryCleanup()
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

    private sealed class FakeStore : ISessionStore
    {
        private readonly IReadOnlyList<SessionStoreRecord> _records;

        public FakeStore(IReadOnlyList<SessionStoreRecord> records)
        {
            _records = records;
        }

        public Task<IReadOnlyList<SessionStoreRecord>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(_records);
    }
}
