using CopilotSessionManager.Core.Sessions;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace CopilotSessionManager.Core.Tests.Sessions;

public class SessionStoreTests : IDisposable
{
    private readonly string _tempRoot;

    public SessionStoreTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "csm-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    [Fact]
    public async Task ListAsync_returns_empty_when_database_does_not_exist()
    {
        var paths = new TestCopilotPaths(Path.Combine(_tempRoot, "missing.db"), _tempRoot);
        var store = new SessionStore(paths, NullLogger<SessionStore>.Instance);

        var result = await store.ListAsync();

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task ListAsync_returns_rows_in_updated_at_descending_order()
    {
        var dbPath = Path.Combine(_tempRoot, "session-store.db");
        await SeedDatabaseAsync(dbPath, new SeedRow[]
        {
            new("11111111-1111-1111-1111-111111111111", "C:\\a", "owner/a", "main", "older", "github",
                "2026-01-01T00:00:00.000Z", "2026-01-02T00:00:00.000Z", 5),
            new("22222222-2222-2222-2222-222222222222", "C:\\b", "owner/b", "feature", "newer", "ado",
                "2026-02-01T00:00:00.000Z", "2026-02-02T00:00:00.000Z", 3),
        });

        var paths = new TestCopilotPaths(dbPath, _tempRoot);
        var store = new SessionStore(paths, NullLogger<SessionStore>.Instance);

        var result = await store.ListAsync();

        result.Should().HaveCount(2);
        result[0].Id.Should().Be("22222222-2222-2222-2222-222222222222");
        result[0].Repository.Should().Be("owner/b");
        result[0].HostType.Should().Be("ado");
        result[0].UpdatedAt.Should().Be(new DateTimeOffset(2026, 2, 2, 0, 0, 0, TimeSpan.Zero));
        result[0].TurnCount.Should().Be(3);
        result[1].Id.Should().Be("11111111-1111-1111-1111-111111111111");
    }

    [Fact]
    public async Task ListAsync_handles_null_columns_gracefully()
    {
        var dbPath = Path.Combine(_tempRoot, "session-store.db");
        await SeedDatabaseAsync(dbPath, new SeedRow[]
        {
            new("33333333-3333-3333-3333-333333333333", null, null, null, null, null,
                "2026-03-01T00:00:00.000Z", "2026-03-01T00:00:00.000Z", 0),
        });

        var paths = new TestCopilotPaths(dbPath, _tempRoot);
        var store = new SessionStore(paths, NullLogger<SessionStore>.Instance);

        var result = await store.ListAsync();

        result.Should().ContainSingle();
        result[0].Cwd.Should().BeNull();
        result[0].Repository.Should().BeNull();
        result[0].HostType.Should().BeNull();
        result[0].TurnCount.Should().Be(0);
    }

    private sealed record SeedRow(
        string Id,
        string? Cwd,
        string? Repository,
        string? Branch,
        string? Summary,
        string? HostType,
        string CreatedAt,
        string UpdatedAt,
        int TurnCount);

    private static async Task SeedDatabaseAsync(string dbPath, IReadOnlyCollection<SeedRow> rows)
    {
        var cs = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
        await using var connection = new SqliteConnection(cs);
        await connection.OpenAsync();

        await using (var schema = connection.CreateCommand())
        {
            schema.CommandText = """
                CREATE TABLE sessions (
                    id TEXT PRIMARY KEY,
                    cwd TEXT,
                    repository TEXT,
                    branch TEXT,
                    summary TEXT,
                    created_at TEXT,
                    updated_at TEXT,
                    host_type TEXT
                );
                CREATE TABLE turns (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    session_id TEXT NOT NULL,
                    turn_index INTEGER NOT NULL,
                    UNIQUE(session_id, turn_index)
                );
                """;
            await schema.ExecuteNonQueryAsync();
        }

        foreach (var row in rows)
        {
            await using var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO sessions (id, cwd, repository, branch, summary, host_type, created_at, updated_at)
                VALUES ($id, $cwd, $repository, $branch, $summary, $hostType, $createdAt, $updatedAt);
                """;
            insert.Parameters.AddWithValue("$id", row.Id);
            insert.Parameters.AddWithValue("$cwd", (object?)row.Cwd ?? DBNull.Value);
            insert.Parameters.AddWithValue("$repository", (object?)row.Repository ?? DBNull.Value);
            insert.Parameters.AddWithValue("$branch", (object?)row.Branch ?? DBNull.Value);
            insert.Parameters.AddWithValue("$summary", (object?)row.Summary ?? DBNull.Value);
            insert.Parameters.AddWithValue("$hostType", (object?)row.HostType ?? DBNull.Value);
            insert.Parameters.AddWithValue("$createdAt", row.CreatedAt);
            insert.Parameters.AddWithValue("$updatedAt", row.UpdatedAt);
            await insert.ExecuteNonQueryAsync();

            for (var i = 0; i < row.TurnCount; i++)
            {
                await using var turn = connection.CreateCommand();
                turn.CommandText = "INSERT INTO turns (session_id, turn_index) VALUES ($id, $idx);";
                turn.Parameters.AddWithValue("$id", row.Id);
                turn.Parameters.AddWithValue("$idx", i);
                await turn.ExecuteNonQueryAsync();
            }
        }
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_tempRoot))
        {
            try
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup; transient locks on Windows are tolerated.
            }
        }
        GC.SuppressFinalize(this);
    }

    private sealed class TestCopilotPaths : ICopilotPaths
    {
        public TestCopilotPaths(string dbPath, string stateDir)
        {
            SessionStoreDatabasePath = dbPath;
            SessionStateDirectory = stateDir;
        }

        public string SessionStoreDatabasePath { get; }
        public string SessionStateDirectory { get; }
    }
}
