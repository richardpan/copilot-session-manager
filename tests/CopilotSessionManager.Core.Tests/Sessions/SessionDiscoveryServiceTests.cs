using CopilotSessionManager.Core.Cli;
using CopilotSessionManager.Core.Cli.Adapters.V1;
using CopilotSessionManager.Core.GitHub.Issues;
using CopilotSessionManager.Core.GitHub.Storage;
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
    public async Task ScanAsync_skips_tombstoned_sessions_when_state_dir_is_missing()
    {
        // Repro for #125: csm hard-deletes a session, removes the on-disk
        // folder, but ADR-002 forbids touching Copilot CLI's session-store.db.
        // Without the tombstone short-circuit the dangling DB row would
        // resurrect the session card on the very next rescan.
        var id = "00000000-0000-0000-0000-0000000000d1";
        var tombstones = new InMemoryTombstones();
        await tombstones.RecordAsync(id);

        var service = CreateService(
            records: new[]
            {
                new SessionStoreRecord(id, @"C:\ws\dead", "github/dead", "main", "DB ghost",
                    "github", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, TurnCount: 1),
            },
            tombstones: tombstones);

        var sessions = await service.ScanAsync();

        sessions.Should().BeEmpty(
            "the tombstone must suppress the dangling DB row that Copilot CLI has not yet pruned");
    }

    [Fact]
    public async Task ScanAsync_self_heals_tombstone_when_state_dir_reappears()
    {
        // Edge case: an id that was tombstoned but whose folder later
        // shows up again (re-import, CLI re-issued the id, manual restore).
        // The tombstone should be cleared so the session is visible again.
        var id = "00000000-0000-0000-0000-0000000000d2";
        WriteSessionFiles(id, copilotVersion: "1.0.43", repository: "github/back", branch: "main");
        var tombstones = new InMemoryTombstones();
        await tombstones.RecordAsync(id);

        var service = CreateService(
            records: Array.Empty<SessionStoreRecord>(),
            tombstones: tombstones);

        var sessions = await service.ScanAsync();

        sessions.Should().ContainSingle();
        sessions[0].Id.Should().Be(id);
        (await tombstones.IsDeletedAsync(id)).Should().BeFalse(
            "tombstone must self-heal once the folder reappears, otherwise we'd suppress a real session forever");
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
    public async Task ScanAsync_marks_session_with_dead_lock_as_Orphaned()
    {
        var id = "00000000-0000-0000-0000-000000000050";
        WriteSessionFiles(id, copilotVersion: "1.0.43", repository: "github/orphan", branch: "main");
        File.WriteAllText(Path.Combine(_stateDir, id, "inuse.99999.lock"), "99999\n");

        var service = CreateService(records: Array.Empty<SessionStoreRecord>());

        var sessions = await service.ScanAsync();

        sessions.Should().ContainSingle();
        sessions[0].Status.Should().Be(SessionStatus.Orphaned);
        sessions[0].Locks.Should().ContainSingle();
        sessions[0].Locks[0].ProcessId.Should().Be(99999);
        sessions[0].Locks[0].IsAlive.Should().BeFalse();
    }

    [Fact]
    public async Task ScanAsync_marks_session_with_live_lock_as_Working()
    {
        var id = "00000000-0000-0000-0000-000000000051";
        WriteSessionFiles(id, copilotVersion: "1.0.43", repository: "github/live", branch: "main");
        File.WriteAllText(Path.Combine(_stateDir, id, "inuse.4242.lock"), "4242\n");

        // Pin "now" close to the synthetic event timestamps so we don't trip
        // the idle threshold.
        var fakeTime = new FakeTimeProvider(new DateTimeOffset(2026, 5, 8, 12, 0, 30, TimeSpan.Zero));
        var service = CreateService(
            records: Array.Empty<SessionStoreRecord>(),
            isAlive: pid => pid == 4242,
            timeProvider: fakeTime);

        var sessions = await service.ScanAsync();

        sessions.Should().ContainSingle();
        sessions[0].Status.Should().Be(SessionStatus.Working);
        sessions[0].Locks.Should().ContainSingle(l => l.IsAlive);
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

    [Fact]
    public async Task ScanAsync_overrides_repository_branch_and_pr_from_store()
    {
        var id = "00000000-0000-0000-0000-000000000201";
        WriteSessionFiles(id, copilotVersion: "1.0.43", repository: "github/auto", branch: "auto-branch");

        var overrides = new InMemoryGitHubLinksStore();
        await overrides.SetAsync(id, new SessionGitHubLinkOverrides(
            RepositoryOverride: "user/manual",
            BranchOverride: "https://github.com/user/manual/tree/manual-branch",
            PullRequestNumberOverride: 99));

        var service = CreateService(records: Array.Empty<SessionStoreRecord>(), overridesStore: overrides);

        var sessions = await service.ScanAsync();

        sessions.Should().ContainSingle();
        var links = sessions[0].GitHubLinks;
        links.Should().NotBeNull();
        links!.RepositoryUrl.Should().Be("https://github.com/user/manual");
        links.BranchUrl.Should().Be("https://github.com/user/manual/tree/manual-branch");
        links.PullRequest.Should().NotBeNull();
        links.PullRequest!.Number.Should().Be(99);
        links.PullRequest.Url.Should().Be("https://github.com/user/manual/pull/99");
    }

    [Fact]
    public async Task ScanAsync_partial_override_only_replaces_non_null_fields()
    {
        var id = "00000000-0000-0000-0000-000000000202";
        WriteSessionFiles(id, copilotVersion: "1.0.43", repository: "github/auto", branch: "auto-branch");

        var overrides = new InMemoryGitHubLinksStore();
        await overrides.SetAsync(id, new SessionGitHubLinkOverrides(
            RepositoryOverride: null,
            BranchOverride: null,
            PullRequestNumberOverride: 17));

        var service = CreateService(records: Array.Empty<SessionStoreRecord>(), overridesStore: overrides);

        var sessions = await service.ScanAsync();

        var links = sessions[0].GitHubLinks!;
        // Repo + branch fall through to the auto-detected resolver output …
        links.RepositoryUrl.Should().Be("https://github.com/github/auto");
        links.BranchUrl.Should().Be("https://github.com/github/auto/tree/auto-branch");
        // … but PR number is overlaid.
        links.PullRequest.Should().NotBeNull();
        links.PullRequest!.Number.Should().Be(17);
        links.PullRequest.Url.Should().Be("https://github.com/github/auto/pull/17");
    }

    [Fact]
    public async Task ScanAsync_when_override_store_throws_returns_unoverridden_links()
    {
        var id = "00000000-0000-0000-0000-000000000203";
        WriteSessionFiles(id, copilotVersion: "1.0.43", repository: "github/auto", branch: "main");

        var service = CreateService(
            records: Array.Empty<SessionStoreRecord>(),
            overridesStore: new ThrowingGitHubLinksStore());

        var sessions = await service.ScanAsync();

        sessions.Should().ContainSingle();
        var links = sessions[0].GitHubLinks!;
        links.RepositoryUrl.Should().Be("https://github.com/github/auto");
        links.BranchUrl.Should().Be("https://github.com/github/auto/tree/main");
        links.PullRequest.Should().BeNull();
    }

    [Fact]
    public async Task ScanAsync_no_override_returns_unmodified_links()
    {
        var id = "00000000-0000-0000-0000-000000000204";
        WriteSessionFiles(id, copilotVersion: "1.0.43", repository: "github/auto", branch: "main");

        var service = CreateService(
            records: Array.Empty<SessionStoreRecord>(),
            overridesStore: new InMemoryGitHubLinksStore());

        var sessions = await service.ScanAsync();

        var links = sessions[0].GitHubLinks!;
        links.RepositoryUrl.Should().Be("https://github.com/github/auto");
        links.BranchUrl.Should().Be("https://github.com/github/auto/tree/main");
        links.PullRequest.Should().BeNull();
    }

    [Fact]
    public async Task ScanAsync_repository_override_as_full_url_passes_through()
    {
        var id = "00000000-0000-0000-0000-000000000205";
        WriteSessionFiles(id, copilotVersion: "1.0.43", repository: "github/auto", branch: "main");

        var overrides = new InMemoryGitHubLinksStore();
        await overrides.SetAsync(id, new SessionGitHubLinkOverrides(
            RepositoryOverride: "https://github.com/user/repo/",
            BranchOverride: null,
            PullRequestNumberOverride: null));

        var service = CreateService(records: Array.Empty<SessionStoreRecord>(), overridesStore: overrides);

        var sessions = await service.ScanAsync();

        sessions[0].GitHubLinks!.RepositoryUrl.Should().Be("https://github.com/user/repo");
    }

    [Fact]
    public async Task ScanAsync_reads_producer_from_first_session_start_event()
    {
        var id = "00000000-0000-0000-0000-000000000099";
        var sessionDir = Path.Combine(_stateDir, id);
        Directory.CreateDirectory(sessionDir);
        File.WriteAllText(Path.Combine(sessionDir, "workspace.yaml"),
            $"id: {id}\ncwd: C:\\ws\\demo\nrepository: github/demo\nhost_type: github\nbranch: main\nsummary: s\ncreated_at: 2026-05-08T12:00:00.000Z\nupdated_at: 2026-05-08T12:30:00.000Z\n");
        var startEvent =
            $$"""{"data":{"copilotVersion":"1.0.43","sessionId":"{{id}}","producer":"copilot-agent","startTime":"2026-05-08T12:00:00.000Z","version":1},"id":"e1","parentId":null,"timestamp":"2026-05-08T12:00:00.000Z","type":"session.start"}""";
        File.WriteAllText(Path.Combine(sessionDir, "events.jsonl"), startEvent + Environment.NewLine);

        var service = CreateService(records: Array.Empty<SessionStoreRecord>());
        var sessions = await service.ScanAsync();

        sessions.Should().ContainSingle();
        sessions[0].Producer.Should().Be("copilot-agent");
    }

    [Fact]
    public async Task ScanAsync_returns_null_producer_when_field_missing()
    {
        var id = "00000000-0000-0000-0000-000000000098";
        // WriteSessionFiles already produces a session.start without "producer".
        WriteSessionFiles(id, copilotVersion: "1.0.43", repository: "github/demo", branch: "main");

        var service = CreateService(records: Array.Empty<SessionStoreRecord>());
        var sessions = await service.ScanAsync();

        sessions.Should().ContainSingle();
        sessions[0].Producer.Should().BeNull();
    }

    [Fact]
    public async Task ScanAsync_returns_null_producer_when_events_file_is_corrupt()
    {
        var id = "00000000-0000-0000-0000-000000000097";
        var sessionDir = Path.Combine(_stateDir, id);
        Directory.CreateDirectory(sessionDir);
        File.WriteAllText(Path.Combine(sessionDir, "workspace.yaml"),
            $"id: {id}\ncwd: C:\\ws\\demo\nrepository: github/demo\nhost_type: github\nbranch: main\nsummary: s\ncreated_at: 2026-05-08T12:00:00.000Z\nupdated_at: 2026-05-08T12:30:00.000Z\n");
        File.WriteAllText(Path.Combine(sessionDir, "events.jsonl"), "{ this is not json" + Environment.NewLine);

        var service = CreateService(records: Array.Empty<SessionStoreRecord>());
        var sessions = await service.ScanAsync();

        sessions.Should().ContainSingle();
        sessions[0].Producer.Should().BeNull();
    }

    private SessionDiscoveryService CreateService(
        IReadOnlyList<SessionStoreRecord> records,
        Func<int, bool>? isAlive = null,
        TimeProvider? timeProvider = null,
        ISessionGitHubLinksStore? overridesStore = null,
        IDeletedSessionRegistry? tombstones = null)
    {
        var paths = new TestPaths(_dbPath, _stateDir);
        var store = new FakeStore(records);
        var registry = new CopilotCliAdapterRegistry(
            new ICopilotCliAdapter[] { new CopilotCliV1Adapter(NullLogger<CopilotCliV1Adapter>.Instance) },
            NullLogger<CopilotCliAdapterRegistry>.Instance);
        var processChecker = new FakeProcessChecker(isAlive ?? (_ => false));
        var lockMonitor = new SessionLockMonitor(paths, processChecker, NullLogger<SessionLockMonitor>.Instance);
        var statusEvaluator = new SessionStatusEvaluator(
            paths,
            registry,
            new StatusDetectionOptions(),
            NullLogger<SessionStatusEvaluator>.Instance);
        _service = new SessionDiscoveryService(
            store,
            registry,
            paths,
            lockMonitor,
            statusEvaluator,
            githubLinkResolver: new CopilotSessionManager.Core.GitHub.GitHubLinkResolver(),
            githubLinksOverrideStore: overridesStore,
            timeProvider ?? TimeProvider.System,
            NullLogger<SessionDiscoveryService>.Instance);
        _service.SetDeletedSessionRegistry(tombstones);
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

    private sealed class FakeProcessChecker : IProcessChecker
    {
        private readonly Func<int, bool> _isAlive;
        public FakeProcessChecker(Func<int, bool> isAlive) => _isAlive = isAlive;
        public bool IsAlive(int pid) => _isAlive(pid);
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public FakeTimeProvider(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
    }

    private sealed class InMemoryTombstones : IDeletedSessionRegistry
    {
        private readonly HashSet<string> _ids = new(StringComparer.OrdinalIgnoreCase);

        public Task RecordAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            lock (_ids)
                _ids.Add(sessionId);
            return Task.CompletedTask;
        }

        public Task ForgetAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            lock (_ids)
                _ids.Remove(sessionId);
            return Task.CompletedTask;
        }

        public Task<bool> IsDeletedAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            lock (_ids)
                return Task.FromResult(_ids.Contains(sessionId));
        }

        public Task<IReadOnlySet<string>> GetAllAsync(CancellationToken cancellationToken = default)
        {
            lock (_ids)
            {
                IReadOnlySet<string> snapshot = new HashSet<string>(_ids, StringComparer.OrdinalIgnoreCase);
                return Task.FromResult(snapshot);
            }
        }
    }

    private sealed class InMemoryGitHubLinksStore : ISessionGitHubLinksStore
    {
        private readonly Dictionary<string, SessionGitHubLinkOverrides> _store = new(StringComparer.OrdinalIgnoreCase);

        public Task<SessionGitHubLinkOverrides?> GetAsync(string sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult(_store.TryGetValue(sessionId, out var v) ? v : null);

        public Task SetAsync(string sessionId, SessionGitHubLinkOverrides overrides, CancellationToken cancellationToken = default)
        {
            if (overrides.HasAnyOverride)
            {
                _store[sessionId] = overrides;
            }
            else
            {
                _store.Remove(sessionId);
            }
            return Task.CompletedTask;
        }

        public Task ClearAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            _store.Remove(sessionId);
            return Task.CompletedTask;
        }

        public Task AddIssueRefAsync(string sessionId, IssueRef issueRef, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task RemoveIssueRefAsync(string sessionId, IssueRef issueRef, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class ThrowingGitHubLinksStore : ISessionGitHubLinksStore
    {
        public Task<SessionGitHubLinkOverrides?> GetAsync(string sessionId, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("simulated store failure");

        public Task SetAsync(string sessionId, SessionGitHubLinkOverrides overrides, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("simulated store failure");

        public Task ClearAsync(string sessionId, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("simulated store failure");

        public Task AddIssueRefAsync(string sessionId, IssueRef issueRef, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("simulated store failure");

        public Task RemoveIssueRefAsync(string sessionId, IssueRef issueRef, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("simulated store failure");
    }
}
